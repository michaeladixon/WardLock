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
/// and never while the vault is locked.
/// </summary>
public partial class MainViewModel
{
    private BrowserBridgeServer? _bridgeServer;

    [ObservableProperty]
    private bool _browserIntegrationInstalled;

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

                // Metadata only — codes are never returned from this action
                var matches = Accounts
                    .Where(acct => DomainMatcher.Matches(domain, acct.Domain))
                    .Select(acct => new
                    {
                        id = acct.Id,
                        issuer = acct.Issuer,
                        label = acct.Label,
                        source = acct.IsShared ? acct.VaultName : "Personal"
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

                account.Refresh();
                if (string.IsNullOrEmpty(account.CurrentCode))
                    return new { ok = false, error = "code-unavailable" };

                ResetIdleTimer();
                StatusMessage = $"Filled {account.DisplayName} in browser ({DomainMatcher.Normalize(domain)}).";
                return new
                {
                    ok = true,
                    code = account.CurrentCode,
                    secondsRemaining = account.SecondsRemaining,
                    issuer = account.Issuer
                };
            }

            default:
                return new { ok = false, error = "unknown-action" };
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
