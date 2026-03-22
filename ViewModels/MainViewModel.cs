using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WardLock.Behaviors;
using WardLock.Models;
using WardLock.Services;
using WardLock.Views;

namespace WardLock.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AccountStore _store = new();
    private readonly DispatcherTimer _timer;
    private readonly List<SharedVaultService> _openVaults = new();

    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    public ObservableCollection<string> OpenVaultNames { get; } = new();

    [ObservableProperty]
    private string _otpAuthInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _footerStatusText = string.Empty;

    private string _vaultIndicatorText = string.Empty;
    public string VaultIndicatorText
    {
        get => _vaultIndicatorText;
        private set => SetProperty(ref _vaultIndicatorText, value);
    }

    private bool _isVaultConnected;
    public bool IsVaultConnected
    {
        get => _isVaultConnected;
        private set => SetProperty(ref _isVaultConnected, value);
    }

    [ObservableProperty]
    private bool _isAddPanelVisible;

    [ObservableProperty]
    private bool _isUnlocked;

    [ObservableProperty]
    private bool _windowsHelloAvailable;

    [ObservableProperty]
    private LockMethod _activeLockMethod;

    [ObservableProperty]
    private string _oAuthDisplayName = string.Empty;

    // Manual entry fields
    [ObservableProperty]
    private string _manualIssuer = string.Empty;

    [ObservableProperty]
    private string _manualLabel = string.Empty;

    [ObservableProperty]
    private string _manualSecret = string.Empty;

    /// <summary>Target for adding accounts: null=personal, vault name=shared vault.</summary>
    [ObservableProperty]
    private string? _addTarget;

    /// <summary>Set by the View so the ViewModel can minimize/restore the window during QR screen capture.</summary>
    public Action? MinimizeWindow { get; set; }
    public Action? RestoreWindow  { get; set; }

    public ObservableCollection<string> AddTargetOptions { get; } = new() { "Personal" };

    // Coordinators to move large feature blocks out of this file
    private readonly Services.SharedVaultCoordinator _vaultCoordinator;
    private readonly Services.QrCoordinator _qrCoordinator;

    private readonly DispatcherTimer _statusFadeTimer;

    public MainViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshAll();

        _statusFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusFadeTimer.Tick += (_, _) =>
        {
            _statusFadeTimer.Stop();
            FooterStatusText = string.Empty;
        };

        OpenVaultNames.CollectionChanged += (_, _) => UpdateVaultIndicator();

        _ = InitializeAsync();
        // instantiate coordinators after basic fields are constructed
        _vaultCoordinator = new Services.SharedVaultCoordinator(_openVaults, Accounts, OpenVaultNames, AddTargetOptions, _store, s => StatusMessage = s);
        _qrCoordinator = new Services.QrCoordinator(_store, Accounts, () => GetSelectedVault(), s => StatusMessage = s);
    }

    private void UpdateVaultIndicator()
    {
        VaultIndicatorText = OpenVaultNames.Count switch
        {
            0 => string.Empty,
            1 => $"● {OpenVaultNames[0]}",
            _ => $"● {OpenVaultNames.Count} vaults"
        };
        IsVaultConnected = OpenVaultNames.Count > 0;
    }

    partial void OnStatusMessageChanged(string value)
    {
        if (!IsUnlocked) return;
        if (string.IsNullOrEmpty(value))
        {
            _statusFadeTimer.Stop();
            FooterStatusText = string.Empty;
            return;
        }
        FooterStatusText = value;
        _statusFadeTimer.Stop();
        _statusFadeTimer.Start();
    }

    private async Task InitializeAsync()
    {
        WindowsHelloAvailable = await WindowsHelloService.IsAvailableAsync();
        ActiveLockMethod      = AppSettings.ActiveLockMethod;
        OAuthDisplayName      = AppSettings.OAuthDisplayName ?? string.Empty;

        if (ActiveLockMethod == LockMethod.None)
        {
            Unlock();
        }
        else
        {
            IsUnlocked    = false;
            StatusMessage = ActiveLockMethod switch
            {
                LockMethod.Password      => "Enter your password to unlock.",
                LockMethod.WindowsHello  => "Verify with Windows Hello or PIN to unlock.",
                LockMethod.OAuthGoogle   => "Sign in with Google to unlock.",
                LockMethod.OAuthMicrosoft => "Sign in with Microsoft to unlock.",
                LockMethod.OAuthFacebook => "Sign in with Facebook to unlock.",
                _                        => "Unlock to view codes."
            };
        }
    }

    // Called from code-behind lock screen (PasswordBox can't data-bind)
    public void TryUnlockWithPassword(string password)
    {
        if (PasswordLockService.Verify(password))
            Unlock();
        else
            StatusMessage = "Incorrect password. Try again.";
    }

    [RelayCommand]
    private async Task RequestUnlock()
    {
        switch (ActiveLockMethod)
        {
            case LockMethod.WindowsHello:
                if (await WindowsHelloService.VerifyAsync())
                    Unlock();
                else
                    StatusMessage = "Verification failed. Try again.";
                break;

            case LockMethod.OAuthGoogle:
            case LockMethod.OAuthMicrosoft:
            case LockMethod.OAuthFacebook:
                await UnlockWithOAuthAsync(ActiveLockMethod);
                break;

            default:
                Unlock();
                break;
        }
    }

    private async Task UnlockWithOAuthAsync(LockMethod method)
    {
        var provider = method switch
        {
            LockMethod.OAuthGoogle    => OAuthService.Provider.Google,
            LockMethod.OAuthMicrosoft => OAuthService.Provider.Microsoft,
            _                         => OAuthService.Provider.Facebook,
        };

        try
        {
            StatusMessage = "Opening browser \u2014 sign in to continue\u2026";
            var identity = await OAuthService.AuthenticateAsync(provider);
            if (identity == null)
            {
                StatusMessage = "Sign-in cancelled or timed out.";
                return;
            }

            var storedSub = AppSettings.OAuthSub;
            if (string.IsNullOrEmpty(storedSub) || identity.Sub == storedSub)
                Unlock();
            else
                StatusMessage = "Signed in as a different account. Use the account you set up WardLock with.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sign-in failed: {ex.Message}";
        }
    }

    private void Unlock()
    {
        IsUnlocked = true;
        ResetIdleTimer();
        _store.Load();

        Accounts.Clear();
        foreach (var acct in _store.Accounts)
            Accounts.Add(new AccountViewModel(acct));

        // Auto-open remembered vaults
        AutoOpenRememberedVaults();

        _timer.Start();
        RefreshAll();

        var vaultCount = _openVaults.Count;
        StatusMessage = vaultCount > 0
            ? $"{Accounts.Count} account(s) loaded. {vaultCount} vault(s) reconnected."
            : $"{Accounts.Count} account(s) loaded.";
    }

    /// <summary>
    /// Attempt to auto-open all remembered vaults using DPAPI-cached passwords.
    /// Silently skips vaults that fail (file missing, password expired, etc.).
    /// </summary>
    private void AutoOpenRememberedVaults()
    {
        var remembered = AppSettings.RememberedVaultPaths;
        foreach (var path in remembered)
        {
            // Skip if already open
            if (_openVaults.Any(v => string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var password = VaultPasswordCache.TryLoad(path);
            if (password == null) continue;

            if (!System.IO.File.Exists(path))
            {
                // File gone — clean up
                VaultPasswordCache.Remove(path);
                AppSettings.RemoveRememberedVaultPath(path);
                continue;
            }

            try
            {
                var vault = new SharedVaultService(path);
                vault.Open(password);
                RegisterVault(vault);
            }
            catch
            {
                // Wrong password (changed by teammate?), corrupted, etc.
                // Remove stale cache so user gets prompted next time
                VaultPasswordCache.Remove(path);
                AppSettings.RemoveRememberedVaultPath(path);
            }
        }
    }

    private void RefreshAll()
    {
        foreach (var vm in Accounts)
        {
            vm.Refresh();
        }

        // Check idle timeout for auto-lock
        CheckIdleTimeout();
    }

    [RelayCommand]
    private void ToggleAddPanel()
    {
        IsAddPanelVisible = !IsAddPanelVisible;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void AddFromUri()
    {
        try
        {
            var input = OtpAuthInput.Trim();
            if (string.IsNullOrEmpty(input))
            {
                StatusMessage = "Paste an otpauth:// URI.";
                return;
            }

            var parsed = AccountStore.ParseOtpAuthUri(input);
            if (IsDuplicate(parsed.Issuer, parsed.Label))
            {
                StatusMessage = "That account already exists.";
                return;
            }

            var targetVault = GetSelectedVault();
            if (targetVault != null)
            {
                var account = targetVault.AddAccountFromUri(input);
                Accounts.Add(new AccountViewModel(account));
                StatusMessage = $"Added {account.Issuer} to vault '{targetVault.VaultName}'";
            }
            else
            {
                _store.Add(parsed);
                Accounts.Add(new AccountViewModel(parsed));
                StatusMessage = $"Added {parsed.Issuer}";
            }

            OtpAuthInput = string.Empty;
            IsAddPanelVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AddManual()
    {
        try
        {
            var secret = ManualSecret.Trim().Replace(" ", "").ToUpperInvariant();
            if (string.IsNullOrEmpty(secret))
            {
                StatusMessage = "Secret is required.";
                return;
            }

            if (IsDuplicate(ManualIssuer.Trim(), ManualLabel.Trim()))
            {
                StatusMessage = "An account with this issuer and label already exists.";
                return;
            }

            var targetVault = GetSelectedVault();
            if (targetVault != null)
            {
                targetVault.AddAccount(ManualIssuer.Trim(), ManualLabel.Trim(), secret);
                var account = targetVault.Accounts.Last();
                Accounts.Add(new AccountViewModel(account));
                StatusMessage = $"Added {ManualIssuer.Trim()} to vault '{targetVault.VaultName}'";
            }
            else
            {
                var account = new AuthAccount
                {
                    Issuer = ManualIssuer.Trim(),
                    Label = ManualLabel.Trim(),
                    EncryptedSecret = SecretVault.Encrypt(secret)
                };
                _store.Add(account);
                Accounts.Add(new AccountViewModel(account));
                StatusMessage = $"Added {account.Issuer}";
            }

            ManualIssuer = string.Empty;
            ManualLabel = string.Empty;
            ManualSecret = string.Empty;
            IsAddPanelVisible = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private SharedVaultService? GetSelectedVault()
    {
        if (string.IsNullOrEmpty(AddTarget) || AddTarget == "Personal")
            return null;
        return _openVaults.FirstOrDefault(v => v.VaultName == AddTarget);
    }

    [RelayCommand]
    private void RemoveAccount(AccountViewModel? vm)
    {
        if (vm == null) return;

        if (vm.IsShared)
        {
            var vault = _openVaults.FirstOrDefault(v => v.VaultName == vm.VaultName);
            vault?.RemoveAccount(vm.Id);
        }
        else
        {
            _store.Remove(vm.Id);
        }

        Accounts.Remove(vm);
        StatusMessage = $"Removed {vm.DisplayName}";
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText().Trim();
                if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
                {
                    OtpAuthInput = text;
                    StatusMessage = "URI pasted from clipboard.";
                }
                else
                {
                    StatusMessage = "Clipboard doesn't contain an otpauth:// URI.";
                }
            }
        }
        catch
        {
            StatusMessage = "Couldn't read clipboard.";
        }
    }

    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // Shared Vaults
    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [RelayCommand]
    private void CreateSharedVault()
    {
        var pwDialog = new PasswordDialog("Choose a password for the shared vault:", true);
        if (pwDialog.ShowDialog() != true || string.IsNullOrEmpty(pwDialog.Password))
            return;

        var dlg = new SaveFileDialog
        {
            Title = "Create Shared Vault",
            Filter = "WardLock Vault|*.wardlock",
            FileName = "team-vault.wardlock"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var vault = SharedVaultService.CreateNew(dlg.FileName, pwDialog.Password);
            RegisterVault(vault);
            AppSettings.AddRecentVaultPath(dlg.FileName);

            // Cache password and remember vault for auto-reconnect
            VaultPasswordCache.Store(dlg.FileName, pwDialog.Password);
            AppSettings.AddRememberedVaultPath(dlg.FileName);

            StatusMessage = $"Created shared vault '{vault.VaultName}'. Share the file with your team.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to create vault: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSharedVault()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Shared Vault",
            Filter = "WardLock Vault|*.wardlock|All files|*.*"
        };

        if (dlg.ShowDialog() != true) return;

        if (_openVaults.Any(v => string.Equals(v.FilePath, dlg.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "That vault is already open.";
            return;
        }

        var pwDialog = new PasswordDialog("Enter the vault password:", false);
        if (pwDialog.ShowDialog() != true || string.IsNullOrEmpty(pwDialog.Password))
            return;

        try
        {
            var vault = new SharedVaultService(dlg.FileName);
            vault.Open(pwDialog.Password);
            RegisterVault(vault);
            AppSettings.AddRecentVaultPath(dlg.FileName);

            // Cache password and remember vault for auto-reconnect
            VaultPasswordCache.Store(dlg.FileName, pwDialog.Password);
            AppSettings.AddRememberedVaultPath(dlg.FileName);

            StatusMessage = $"Opened vault '{vault.VaultName}' with {vault.Accounts.Count} account(s). Vault will reconnect automatically.";
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            StatusMessage = "Wrong password or corrupted vault file.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open vault: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CloseSharedVault(string? vaultName)
    {
        if (string.IsNullOrEmpty(vaultName)) return;

        var vault = _openVaults.FirstOrDefault(v => v.VaultName == vaultName);
        if (vault == null) return;

        var toRemove = Accounts.Where(a => a.VaultName == vaultName).ToList();
        foreach (var vm in toRemove)
            Accounts.Remove(vm);

        // Ask if user wants to forget the vault (stop auto-reconnecting)
        var isRemembered = AppSettings.RememberedVaultPaths
            .Any(p => string.Equals(p, vault.FilePath, StringComparison.OrdinalIgnoreCase));

        if (isRemembered)
        {
            var result = System.Windows.MessageBox.Show(
                $"Stop auto-connecting to '{vaultName}' on startup?\n\nChoose Yes to forget, No to reconnect next time.",
                "Forget Vault?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.No);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                VaultPasswordCache.Remove(vault.FilePath);
                AppSettings.RemoveRememberedVaultPath(vault.FilePath);
            }
        }

        vault.Dispose();
        _openVaults.Remove(vault);
        OpenVaultNames.Remove(vaultName);
        AddTargetOptions.Remove(vaultName);

        StatusMessage = $"Closed vault '{vaultName}'.";
    }

    private void RegisterVault(SharedVaultService vault)
    {
        _openVaults.Add(vault);
        OpenVaultNames.Add(vault.VaultName);
        AddTargetOptions.Add(vault.VaultName);

        foreach (var acct in vault.Accounts)
            Accounts.Add(new AccountViewModel(acct));

        vault.ExternalChange += () =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                var old = Accounts.Where(a => a.VaultName == vault.VaultName).ToList();
                foreach (var vm in old)
                    Accounts.Remove(vm);

                foreach (var acct in vault.Accounts)
                    Accounts.Add(new AccountViewModel(acct));

                StatusMessage = $"Vault '{vault.VaultName}' updated by another user.";
            });
        };
    }

    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // QR Scanning
    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [RelayCommand]
    private async Task ScanQrFromScreen()
    {
        try
        {
            MinimizeWindow?.Invoke();
            await Task.Delay(300);

            var uri = QrScanner.DecodeFromScreen();
            if (uri != null)
            {
                RestoreWindow?.Invoke();
                AddFromScannedUri(uri);
                return;
            }

            StatusMessage = "QR not found on screen. Select the QR code area...";
            var overlay = new ScreenCaptureOverlay();
            overlay.ShowDialog();

            RestoreWindow?.Invoke();

            if (overlay.SelectedRegion is { } region)
            {
                uri = QrScanner.DecodeFromRegion(region.X, region.Y, region.Width, region.Height);
                if (uri != null)
                {
                    AddFromScannedUri(uri);
                    return;
                }
            }

            StatusMessage = "No QR code found. Try scanning from an image file instead.";
        }
        catch (Exception ex)
        {
            RestoreWindow?.Invoke();
            StatusMessage = $"Scan error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ScanQrFromFile()
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select QR Code Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                var uri = QrScanner.DecodeFromFile(dlg.FileName);
                if (uri != null)
                    AddFromScannedUri(uri);
                else
                    StatusMessage = "No otpauth:// QR code found in image.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ScanQrFromClipboardImage()
    {
        try
        {
            var uri = QrScanner.DecodeFromClipboard();
            if (uri != null)
                AddFromScannedUri(uri);
            else
                StatusMessage = "No otpauth:// QR code found in clipboard image.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
    }

    private void AddFromScannedUri(string uri)
    {
        if (GoogleAuthMigrationDecoder.IsMigrationUri(uri))
        {
            var otpUris = GoogleAuthMigrationDecoder.ParseMigrationUri(uri);
            int added = 0, skipped = 0;
            foreach (var otpUri in otpUris)
            {
                if (AddSingleOtpUri(otpUri)) added++; else skipped++;
            }
            IsAddPanelVisible = false;
            StatusMessage = skipped > 0
                ? $"Imported {added} account(s) from Google Authenticator. {skipped} duplicate(s) skipped."
                : $"Imported {added} account(s) from Google Authenticator.";
            return;
        }

        if (AddSingleOtpUri(uri))
            IsAddPanelVisible = false;
    }

    private bool AddSingleOtpUri(string uri)
    {
        var parsed = AccountStore.ParseOtpAuthUri(uri);
        if (IsDuplicate(parsed.Issuer, parsed.Label))
        {
            StatusMessage = $"Skipped duplicate: {parsed.Issuer} ({parsed.Label})";
            return false;
        }

        var targetVault = GetSelectedVault();
        if (targetVault != null)
        {
            var account = targetVault.AddAccountFromUri(uri);
            Accounts.Add(new AccountViewModel(account));
            StatusMessage = $"Added {account.Issuer} to vault '{targetVault.VaultName}'";
        }
        else
        {
            _store.Add(parsed);
            Accounts.Add(new AccountViewModel(parsed));
            StatusMessage = $"Added {parsed.Issuer}";
        }
        return true;
    }

    private bool IsDuplicate(string issuer, string label)
        => Accounts.Any(a =>
            string.Equals(a.Issuer, issuer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Label, label, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Decrypts a personal account's DPAPI secret and moves it to the specified shared vault.
    /// </summary>
    public void MoveAccountToVault(AccountViewModel accountVm, string vaultName)
    {
        if (accountVm.IsShared) return;

        var vault = _openVaults.FirstOrDefault(v => v.VaultName == vaultName);
        if (vault == null) return;

        var personal = _store.Accounts.FirstOrDefault(a => a.Id == accountVm.Id);
        if (personal == null) return;

        var plaintext = SecretVault.Decrypt(personal.EncryptedSecret);
        vault.AddAccount(personal.Issuer, personal.Label, plaintext,
            personal.Digits, personal.Period, personal.Algorithm);

        _store.Remove(personal.Id);

        Accounts.Remove(accountVm);
        Accounts.Add(new AccountViewModel(vault.Accounts.Last()));

        StatusMessage = $"Moved {personal.Issuer} to '{vaultName}'.";
    }

    public void MoveAccountToLocal(AccountViewModel accountVm)
    {
        if (!accountVm.IsShared) return;

        var vault = _openVaults.FirstOrDefault(v => v.VaultName == accountVm.VaultName);
        if (vault == null) return;

        var vaultAccount = vault.Accounts.FirstOrDefault(a => a.Id == accountVm.Id);
        if (vaultAccount == null) return;

        var plaintext = vaultAccount.PlaintextSecret ?? string.Empty;
        var personal = new AuthAccount
        {
            Issuer          = vaultAccount.Issuer,
            Label           = vaultAccount.Label,
            EncryptedSecret = SecretVault.Encrypt(plaintext),
            Digits          = vaultAccount.Digits,
            Period          = vaultAccount.Period,
            Algorithm       = vaultAccount.Algorithm
        };

        vault.RemoveAccount(vaultAccount.Id);
        _store.Add(personal);

        Accounts.Remove(accountVm);
        Accounts.Add(new AccountViewModel(personal));

        StatusMessage = $"Moved {personal.Issuer} to Personal.";
    }

    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // Export / Import
    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [RelayCommand]
    private void ExportAccounts()
    {
        if (_store.Accounts.Count == 0)
        {
            StatusMessage = "No personal accounts to export.";
            return;
        }

        var pwDialog = new PasswordDialog("Choose a password to encrypt your backup:", true);
        if (pwDialog.ShowDialog() != true || string.IsNullOrEmpty(pwDialog.Password))
            return;

        var dlg = new SaveFileDialog
        {
            Title = "Export Encrypted Backup",
            Filter = "WardLock Backup|*.wardlock|All files|*.*",
            FileName = $"wardlock-backup-{DateTime.Now:yyyy-MM-dd}.wardlock"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                ExportImportService.Export(_store.Accounts, dlg.FileName, pwDialog.Password);
                StatusMessage = $"Exported {_store.Accounts.Count} account(s) to {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export failed: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void ImportAccounts()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import Encrypted Backup",
            Filter = "WardLock Backup|*.wardlock|All files|*.*"
        };

        if (dlg.ShowDialog() != true) return;

        var pwDialog = new PasswordDialog("Enter the backup password:", false);
        if (pwDialog.ShowDialog() != true || string.IsNullOrEmpty(pwDialog.Password))
            return;

        try
        {
            var imported = ExportImportService.Import(dlg.FileName, pwDialog.Password);
            _store.AddRange(imported);

            foreach (var acct in imported)
                Accounts.Add(new AccountViewModel(acct));

            StatusMessage = $"Imported {imported.Count} account(s).";
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            StatusMessage = "Wrong password or corrupted backup file.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // Drag-and-Drop Reorder
    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [RelayCommand]
    private void MoveAccount(MoveArgs? args)
    {
        if (args == null) return;
        var (from, to) = args;
        if (from < 0 || from >= Accounts.Count || to < 0 || to >= Accounts.Count) return;

        var fromVault = Accounts[from].VaultName;
        var toVault = Accounts[to].VaultName;
        if (fromVault != toVault) return;

        if (fromVault == null)
        {
            _store.Move(from, to);
        }

        Accounts.Move(from, to);
    }

    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // Lock Method Configuration
    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [RelayCommand]
    private void SetLockNone()
    {
        PasswordLockService.Clear();
        AppSettings.OAuthSub         = null;
        AppSettings.OAuthDisplayName = null;
        ActiveLockMethod             = LockMethod.None;
        AppSettings.ActiveLockMethod = LockMethod.None;
        StatusMessage = "App lock disabled.";
    }

    [RelayCommand]
    private void SetLockPassword()
    {
        var dlg = new PasswordDialog("Choose an app unlock password:", requireConfirmation: true);
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.Password)) return;

        PasswordLockService.Set(dlg.Password);
        ActiveLockMethod             = LockMethod.Password;
        AppSettings.ActiveLockMethod = LockMethod.Password;
        StatusMessage = "Password lock enabled.";
    }

    [RelayCommand]
    private async Task SetLockWindowsHello()
    {
        if (!WindowsHelloAvailable)
        {
            StatusMessage = "Windows Hello is not available on this device.";
            return;
        }
        if (!await WindowsHelloService.VerifyAsync("Verify to enable Windows Hello lock"))
            return;

        ActiveLockMethod             = LockMethod.WindowsHello;
        AppSettings.ActiveLockMethod = LockMethod.WindowsHello;
        StatusMessage = "Windows Hello / PIN lock enabled.";
    }

    [RelayCommand]
    private async Task SetLockOAuth(string? providerName)
    {
        if (!Enum.TryParse<OAuthService.Provider>(providerName, out var provider)) return;

        var lockMethod = provider switch
        {
            OAuthService.Provider.Google    => LockMethod.OAuthGoogle,
            OAuthService.Provider.Microsoft => LockMethod.OAuthMicrosoft,
            _                               => LockMethod.OAuthFacebook,
        };

        if (!OAuthService.IsConfigured(provider))
        {
            StatusMessage = $"{provider} client ID is not configured. Add it to OAuthService.cs before using this option.";
            return;
        }

        try
        {
            StatusMessage = $"Opening browser to set up {provider} sign-in\u2026";
            var identity = await OAuthService.AuthenticateAsync(provider);
            if (identity == null)
            {
                StatusMessage = "Setup cancelled.";
                return;
            }

            AppSettings.OAuthSub         = identity.Sub;
            AppSettings.OAuthDisplayName = string.IsNullOrEmpty(identity.Name) ? identity.Email : identity.Name;
            OAuthDisplayName             = AppSettings.OAuthDisplayName ?? string.Empty;
            ActiveLockMethod             = lockMethod;
            AppSettings.ActiveLockMethod = lockMethod;
            StatusMessage = $"{provider} lock enabled for {identity.Email}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Setup failed: {ex.Message}";
        }
    }

    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
    // Cleanup
    // \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    public void Shutdown()
    {
        foreach (var vault in _openVaults)
            vault.Dispose();
        _openVaults.Clear();
    }
}
