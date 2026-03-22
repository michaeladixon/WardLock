using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WardLock.ViewModels;

/// <summary>
/// Search/filter and auto-lock functionality split from MainViewModel.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    private string _searchText = string.Empty;

    private ICollectionView? _filteredView;

    /// <summary>
    /// Filtered view of Accounts for the ListBox. Filters on Issuer, Label, and VaultName.
    /// </summary>
    public ICollectionView FilteredAccountsView
    {
        get
        {
            if (_filteredView == null)
            {
                _filteredView = CollectionViewSource.GetDefaultView(Accounts);
                _filteredView.Filter = FilterAccount;
            }
            return _filteredView;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _filteredView?.Refresh();
    }

    private bool FilterAccount(object obj)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (obj is not AccountViewModel vm) return false;

        var term = SearchText.Trim();
        return vm.Issuer.Contains(term, StringComparison.OrdinalIgnoreCase)
            || vm.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (vm.VaultName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // ──────────────────────────────────────────────
    // Auto-Lock on Idle
    // ──────────────────────────────────────────────

    private DateTime _lastActivityTime = DateTime.UtcNow;

    /// <summary>
    /// Called by the View on any user input (mouse move, key press).
    /// </summary>
    public void ResetIdleTimer() => _lastActivityTime = DateTime.UtcNow;

    private void CheckIdleTimeout()
    {
        if (!IsUnlocked) return;
        if (ActiveLockMethod == Services.LockMethod.None) return;

        var timeout = Services.AppSettings.AutoLockTimeoutMinutes;
        if (timeout <= 0) return;

        if ((DateTime.UtcNow - _lastActivityTime).TotalMinutes >= timeout)
        {
            Lock();
        }
    }

    /// <summary>
    /// Re-lock the app. Clears codes from display and shows the lock screen.
    /// </summary>
    public void Lock()
    {
        if (!IsUnlocked) return;

        _timer.Stop();
        Accounts.Clear();
        SearchText = string.Empty;
        IsUnlocked = false;
        StatusMessage = ActiveLockMethod switch
        {
            Services.LockMethod.Password      => "Locked after inactivity. Enter your password.",
            Services.LockMethod.WindowsHello   => "Locked after inactivity. Verify with Windows Hello.",
            Services.LockMethod.OAuthGoogle    => "Locked after inactivity. Sign in with Google.",
            Services.LockMethod.OAuthMicrosoft => "Locked after inactivity. Sign in with Microsoft.",
            Services.LockMethod.OAuthFacebook  => "Locked after inactivity. Sign in with Facebook.",
            _                                  => "Locked."
        };
    }
}
