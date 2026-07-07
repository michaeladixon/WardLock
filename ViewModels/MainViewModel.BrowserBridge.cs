using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WardLock.Services;
using WardLock.Services.BrowserBridge;

namespace WardLock.ViewModels;

/// <summary>
/// Browser integration (issue #1, Tier 2): answers requests from the WardLock
/// browser extension via the named-pipe bridge. Codes are only released for
/// accounts whose stored fill domain matches the requesting page (DomainMatcher),
/// and never while the vault is locked. Accounts flagged RequireApproval — and
/// any browser profile inside its first 24h of pairing — additionally need the
/// number-matched out-of-band approval: the popup shows a 2-digit number, the
/// user types it into this window, and only then is the code released.
/// </summary>
public partial class MainViewModel
{
    private BrowserBridgeServer? _bridgeServer;
    private readonly FillApprovalService _fillApproval = new();

    [ObservableProperty]
    private bool _browserIntegrationInstalled;

    // ── Approval banner state ──

    [ObservableProperty]
    private bool _hasPendingApproval;

    [ObservableProperty]
    private string _approvalPromptText = string.Empty;

    [ObservableProperty]
    private string _approvalInput = string.Empty;

    [ObservableProperty]
    private int _approvalSecondsRemaining;

    [ObservableProperty]
    private string _approvalFeedback = string.Empty;

    private void StartBrowserBridge()
    {
        BrowserIntegrationInstalled = BrowserIntegrationInstaller.IsInstalled();
        _bridgeServer = new BrowserBridgeServer(HandleBridgeRequest);
    }

    [RelayCommand]
    private void EnableBrowserIntegration()
    {
        try
        {
            BrowserIntegrationInstaller.Install();
            BrowserIntegrationInstalled = true;
            StatusMessage = "Browser integration enabled. Load the extension from the BrowserExtension folder.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to enable browser integration: {ex.Message}";
        }
    }

    /// <summary>Set or clear the browser-fill domain for an account and persist it.</summary>
    public void SetAccountDomain(AccountViewModel vm, string? rawDomain)
    {
        var domain = DomainMatcher.Normalize(rawDomain);
        if (domain == null && !string.IsNullOrWhiteSpace(rawDomain))
        {
            StatusMessage = "Enter a domain like github.com (or leave empty to clear).";
            return;
        }

        if (vm.IsShared)
        {
            var vault = _openVaults.FirstOrDefault(v => v.VaultName == vm.VaultName);
            if (vault == null) return;
            vault.UpdateAccountDomain(vm.Id, domain);
        }
        else
        {
            var account = _store.Accounts.FirstOrDefault(a => a.Id == vm.Id);
            if (account == null) return;
            account.Domain = domain;
            _store.Save();
        }

        vm.NotifyDomainChanged();
        StatusMessage = domain == null
            ? $"Cleared fill domain for {vm.DisplayName}."
            : $"{vm.DisplayName} will fill codes on {domain}.";
    }

    /// <summary>Toggle the number-matched fill approval requirement and persist it.</summary>
    public void SetAccountApproval(AccountViewModel vm, bool requireApproval)
    {
        if (vm.IsShared)
        {
            var vault = _openVaults.FirstOrDefault(v => v.VaultName == vm.VaultName);
            if (vault == null) return;
            vault.UpdateAccountApproval(vm.Id, requireApproval);
        }
        else
        {
            var account = _store.Accounts.FirstOrDefault(a => a.Id == vm.Id);
            if (account == null) return;
            account.RequireApproval = requireApproval;
            _store.Save();
        }

        vm.NotifyApprovalChanged();
        StatusMessage = requireApproval
            ? $"Browser fills for {vm.DisplayName} now require number-matched approval."
            : $"{vm.DisplayName} fills without approval.";
    }

    // ── Approval banner handling (user side of the number match) ──

    partial void OnApprovalInputChanged(string value)
    {
        if (!HasPendingApproval || value.Trim().Length < 2) return;

        var challenge = _fillApproval.Pending;
        switch (_fillApproval.TryApprove(value))
        {
            case ApprovalAttemptResult.Approved:
                ClearApprovalBanner();
                ResetIdleTimer();
                StatusMessage = $"Approved — code for {challenge!.AccountDisplayName} released to {challenge.Domain}.";
                break;

            case ApprovalAttemptResult.WrongNumber:
                ApprovalInput = string.Empty;
                ApprovalFeedback =
                    $"Wrong number — {FillApprovalService.MaxWrongAttempts - challenge!.WrongAttempts} attempt(s) left.";
                break;

            case ApprovalAttemptResult.Denied:
                AuditFillApproval(challenge!, AuditAction.FillApprovalDenied, "wrong number");
                ClearApprovalBanner();
                StatusMessage = "Browser fill denied: wrong number entered 3 times.";
                break;

            case ApprovalAttemptResult.NoChallenge:
                ClearApprovalBanner();
                break;
        }
    }

    [RelayCommand]
    private void DenyFillApproval()
    {
        var challenge = _fillApproval.Pending;
        if (challenge != null)
            AuditFillApproval(challenge, AuditAction.FillApprovalDenied, "denied in app");
        _fillApproval.DenyPending("user");
        ClearApprovalBanner();
        ResetIdleTimer();
        StatusMessage = "Browser fill request denied.";
    }

    /// <summary>Ticked by the refresh timer: counts the banner down and clears it on expiry.</summary>
    private void UpdateApprovalTick()
    {
        if (!HasPendingApproval) return;
        ApprovalSecondsRemaining = _fillApproval.PendingSecondsRemaining;
        if (_fillApproval.Pending == null) ClearApprovalBanner();
    }

    /// <summary>Called from Lock(): a locked vault never keeps a live challenge.</summary>
    private void CancelPendingApproval()
    {
        _fillApproval.DenyPending("locked");
        ClearApprovalBanner();
    }

    private void ClearApprovalBanner()
    {
        HasPendingApproval = false;
        ApprovalPromptText = string.Empty;
        ApprovalInput = string.Empty;
        ApprovalFeedback = string.Empty;
    }

    private void AuditFillApproval(FillChallenge challenge, AuditAction action, string detail)
    {
        var vm = Accounts.FirstOrDefault(a => a.Id == challenge.AccountId);
        if (vm != null)
            LogVaultCodeAccess(vm, action, $"{challenge.Domain} — {detail}");
    }

    // ── Bridge request handling ──

    /// <summary>Called on a pipe thread; marshals onto the UI thread.</summary>
    private object HandleBridgeRequest(JsonDocument request)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return new { ok = false, error = "app-shutting-down" };
        return dispatcher.Invoke(() => HandleBridgeRequestCore(request));
    }

    private object HandleBridgeRequestCore(JsonDocument request)
    {
        var root = request.RootElement;
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;

        switch (action)
        {
            case "status":
                return new { ok = true, app = "WardLock", locked = !IsUnlocked };

            case "accounts":
            {
                if (!IsUnlocked) return new { ok = false, error = "locked" };

                var domain = GetString(root, "domain");
                if (DomainMatcher.Normalize(domain) == null)
                    return new { ok = false, error = "invalid-domain" };

                // First contact starts the pairing probation clock
                var probation = AppSettings.IsBrowserClientInProbation(GetString(root, "client"));

                // Metadata only — codes are never returned from this action
                var matches = Accounts
                    .Where(acct => DomainMatcher.Matches(domain, acct.Domain))
                    .Select(acct => new
                    {
                        id = acct.Id,
                        issuer = acct.Issuer,
                        label = acct.Label,
                        source = acct.IsShared ? acct.VaultName : "Personal",
                        requiresApproval = acct.RequireApproval || probation
                    })
                    .ToList();
                return new { ok = true, accounts = matches };
            }

            case "fill-code":
            {
                if (!IsUnlocked) return new { ok = false, error = "locked" };

                var id = GetString(root, "id");
                var domain = GetString(root, "domain");
                var account = Accounts.FirstOrDefault(acct => acct.Id == id);
                if (account == null)
                    return new { ok = false, error = "unknown-account" };

                // Re-validate — never trust the extension's account choice alone
                if (!DomainMatcher.Matches(domain, account.Domain))
                    return new { ok = false, error = "domain-mismatch" };

                var clientId = GetString(root, "client") ?? string.Empty;
                if (account.RequireApproval || AppSettings.IsBrowserClientInProbation(clientId))
                {
                    var challenge = _fillApproval.Begin(account.Id, account.DisplayName,
                        DomainMatcher.Normalize(domain)!, clientId);
                    ShowApprovalBanner(challenge);
                    AuditFillApproval(challenge, AuditAction.FillApprovalRequested,
                        account.RequireApproval ? "approval-required account" : "new browser pairing");
                    return new
                    {
                        ok = false,
                        error = "approval-required",
                        challengeId = challenge.Id,
                        challenge = challenge.Number,
                        expiresIn = FillApprovalService.EntryWindowSeconds
                    };
                }

                return ReleaseCode(account, domain, "");
            }

            case "approval-status":
            {
                if (!IsUnlocked) return new { ok = false, error = "locked" };

                var challengeId = GetString(root, "challengeId") ?? string.Empty;
                var clientId = GetString(root, "client") ?? string.Empty;

                switch (_fillApproval.GetStatus(challengeId, clientId))
                {
                    case ChallengeStatus.Pending:
                        return new
                        {
                            ok = true,
                            status = "pending",
                            secondsRemaining = _fillApproval.PendingSecondsRemaining
                        };

                    case ChallengeStatus.Approved:
                    {
                        var challenge = _fillApproval.TryConsume(challengeId, clientId);
                        if (challenge == null) return new { ok = true, status = "expired" };

                        var account = Accounts.FirstOrDefault(acct => acct.Id == challenge.AccountId);
                        // Account or its domain may have changed while the challenge was open
                        if (account == null || !DomainMatcher.Matches(challenge.Domain, account.Domain))
                            return new { ok = true, status = "denied", reason = "account-changed" };

                        return ReleaseCode(account, challenge.Domain, " (number-matched approval)", "approved");
                    }

                    case ChallengeStatus.Denied:
                        return new { ok = true, status = "denied", reason = "" };

                    case ChallengeStatus.Expired:
                        return new { ok = true, status = "expired" };

                    default:
                        return new { ok = true, status = "unknown" };
                }
            }

            default:
                return new { ok = false, error = "unknown-action" };
        }
    }

    /// <summary>Mint the current code for the browser and audit the release.</summary>
    private object ReleaseCode(AccountViewModel account, string? domain, string auditSuffix, string? status = null)
    {
        account.Refresh();
        if (string.IsNullOrEmpty(account.CurrentCode))
            return new { ok = false, error = "code-unavailable" };

        var normalized = DomainMatcher.Normalize(domain) ?? string.Empty;
        ResetIdleTimer();
        LogVaultCodeAccess(account, AuditAction.CodeFilledInBrowser, normalized + auditSuffix);
        StatusMessage = $"Filled {account.DisplayName} in browser ({normalized}).";
        return new
        {
            ok = true,
            status,
            code = account.CurrentCode,
            secondsRemaining = account.SecondsRemaining,
            issuer = account.Issuer
        };
    }

    private void ShowApprovalBanner(FillChallenge challenge)
    {
        ApprovalInput = string.Empty;
        ApprovalFeedback = string.Empty;
        ApprovalPromptText =
            $"Browser fill request: {challenge.AccountDisplayName} on {challenge.Domain}. " +
            "Type the 2-digit number shown in the browser popup:";
        ApprovalSecondsRemaining = FillApprovalService.EntryWindowSeconds;
        HasPendingApproval = true;
        RestoreWindow?.Invoke();
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
