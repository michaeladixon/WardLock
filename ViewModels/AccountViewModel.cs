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
    public string SourceLabel => IsShared ? $"🔗 {VaultName}" : "🔒 Personal";

    [ObservableProperty]
    private string _currentCode = string.Empty;

    [ObservableProperty]
    private int _secondsRemaining;

    [ObservableProperty]
    private double _progressPercent = 100;

    [ObservableProperty]
    private bool _justCopied;

    public void Refresh()
    {
        CurrentCode = TotpGenerator.GenerateCode(_account);
        SecondsRemaining = TotpGenerator.SecondsRemaining(Period);
        ProgressPercent = (double)SecondsRemaining / Period * 100;
        OnPropertyChanged(nameof(FormattedCode));
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

    [RelayCommand]
    private async Task CopyToClipboard()
    {
        System.Windows.Clipboard.SetText(CurrentCode);
        JustCopied = true;
        await Task.Delay(1500);
        JustCopied = false;
    }
}
