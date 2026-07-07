using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WardLock.Models;
using WardLock.Services;

namespace WardLock.ViewModels;

public partial class AccountViewModel : ObservableObject
{
    private readonly AuthAccount _account;

    public AccountViewModel(AuthAccount account)
    {
        _account = account;
    }

    public string Id => _account.Id;
    public string Issuer => _account.Issuer;
    public string Label => _account.Label;
    public string DisplayName => string.IsNullOrEmpty(Issuer) ? Label : $"{Issuer} ({Label})";
    public int Period => _account.Period;

    /// <summary>Null for personal accounts, vault name for shared accounts.</summary>
    public string? VaultName => _account.VaultName;
    public bool IsShared => _account.VaultName != null;

    /// <summary>Vault opened with the viewer password: codes only, no seed in this process.</summary>
    public bool IsViewerCopy => _account.CodeWindow != null;

    public string SourceLabel => !IsShared ? "\ud83d\udd12 Personal"
        : IsViewerCopy ? $"\ud83d\udc41 {VaultName}"
        : $"\ud83d\udd17 {VaultName}";

    /// <summary>Viewer window lapsed \u2014 no admin refreshed the vault within the horizon.</summary>
    public bool IsCodeStale => IsViewerCopy && string.IsNullOrEmpty(CurrentCode);

    /// <summary>Registrable domain this account fills in the browser, or null.</summary>
    public string? Domain => _account.Domain;
    public string DomainBadge => string.IsNullOrEmpty(Domain) ? string.Empty : $"\ud83c\udf10 {Domain}";

    /// <summary>Call after the underlying account's Domain was changed and persisted.</summary>
    public void NotifyDomainChanged()
    {
        OnPropertyChanged(nameof(Domain));
        OnPropertyChanged(nameof(DomainBadge));
    }

    /// <summary>Browser fills require the number-matched out-of-band approval.</summary>
    public bool RequireApproval => _account.RequireApproval;
    public string ApprovalBadge => RequireApproval ? "🛡 approval" : string.Empty;

    /// <summary>Call after the underlying account's RequireApproval was changed and persisted.</summary>
    public void NotifyApprovalChanged()
    {
        OnPropertyChanged(nameof(RequireApproval));
        OnPropertyChanged(nameof(ApprovalBadge));
    }

    /// <summary>
    /// Code color: mauve (#cba6f7) for shared vault accounts,
    /// green (#a6e3a1) for personal accounts.
    /// </summary>
    public string CodeColor => IsShared ? "#cba6f7" : "#a6e3a1";

    [ObservableProperty]
    private string _currentCode = string.Empty;

    [ObservableProperty]
    private int _secondsRemaining;

    [ObservableProperty]
    private double _progressPercent = 100;

    [ObservableProperty]
    private bool _justCopied;

    partial void OnJustCopiedChanged(bool value) => OnPropertyChanged(nameof(DisplayCode));

    public void Refresh()
    {
        CurrentCode = TotpGenerator.GenerateCode(_account);
        SecondsRemaining = TotpGenerator.SecondsRemaining(Period);
        ProgressPercent = (double)SecondsRemaining / Period * 100;
        OnPropertyChanged(nameof(FormattedCode));
        OnPropertyChanged(nameof(DisplayCode));
    }

    /// <summary>
    /// Formatted code with space in the middle for readability: "123 456"
    /// </summary>
    public string FormattedCode
    {
        get
        {
            if (CurrentCode.Length == 6)
                return $"{CurrentCode[..3]} {CurrentCode[3..]}";
            if (CurrentCode.Length == 8)
                return $"{CurrentCode[..4]} {CurrentCode[4..]}";
            return CurrentCode;
        }
    }

    /// <summary>
    /// Shows "Copied!" during the copy feedback window, a stale marker when a
    /// viewer's precomputed window has lapsed, otherwise the formatted code.
    /// </summary>
    public string DisplayCode => JustCopied ? "Copied!" : IsCodeStale ? "code expired" : FormattedCode;

    /// <summary>Raised when a shared-vault account's code is copied, for the audit trail.</summary>
    public static event Action<AccountViewModel>? SharedCodeCopied;

    [RelayCommand]
    private async Task CopyToClipboard()
    {
        if (string.IsNullOrEmpty(CurrentCode)) return; // stale viewer window / decrypt failure
        System.Windows.Clipboard.SetText(CurrentCode);
        if (IsShared)
            SharedCodeCopied?.Invoke(this);
        JustCopied = true;
        await Task.Delay(1500);
        JustCopied = false;
    }
}
