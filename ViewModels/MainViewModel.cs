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
    private readonly List<SharedVaultService> _openVaults = [];

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];
    public ObservableCollection<string> OpenVaultNames { get; } = [];

    [ObservableProperty]
    private string _otpAuthInput = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isAddPanelVisible;

    [ObservableProperty]
    private bool _isUnlocked;

    [ObservableProperty]
    private bool _windowsHelloAvailable;

    [ObservableProperty]
    private bool _windowsHelloEnabled;

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

    public ObservableCollection<string> AddTargetOptions { get; } = ["Personal"];

    public MainViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshAll();

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        WindowsHelloAvailable = await WindowsHelloService.IsAvailableAsync();
        WindowsHelloEnabled = AppSettings.IsWindowsHelloEnabled;

        if (WindowsHelloAvailable && WindowsHelloEnabled)
        {
            IsUnlocked = false;
            StatusMessage = "Unlock with Windows Hello to view codes.";
        }
        else
        {
            Unlock();
        }
    }

    [RelayCommand]
    private async Task RequestUnlock()
    {
        if (await WindowsHelloService.VerifyAsync())
            Unlock();
        else
            StatusMessage = "Verification failed. Try again.";
    }

    private void Unlock()
    {
        IsUnlocked = true;
        _store.Load();

        Accounts.Clear();
        foreach (var acct in _store.Accounts)
            Accounts.Add(new AccountViewModel(acct));

        _timer.Start();
        RefreshAll();
        StatusMessage = $"{Accounts.Count} account(s) loaded.";
    }

    private void RefreshAll()
    {
        foreach (var vm in Accounts)
        {
            vm.Refresh();
        }
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
            // Remove from shared vault
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

    // ──────────────────────────────────────────────
    // Shared Vaults
    // ──────────────────────────────────────────────

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

        // Check if already open
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
            StatusMessage = $"Opened vault '{vault.VaultName}' with {vault.Accounts.Count} account(s).";
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

        // Remove vault accounts from display
        var toRemove = Accounts.Where(a => a.VaultName == vaultName).ToList();
        foreach (var vm in toRemove)
            Accounts.Remove(vm);

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

        // Add vault accounts to display
        foreach (var acct in vault.Accounts)
            Accounts.Add(new AccountViewModel(acct));

        // Listen for external changes (teammate edits)
        vault.ExternalChange += () =>
        {
            // Marshal to UI thread
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                // Remove old vault accounts
                var old = Accounts.Where(a => a.VaultName == vault.VaultName).ToList();
                foreach (var vm in old)
                    Accounts.Remove(vm);

                // Add refreshed accounts
                foreach (var acct in vault.Accounts)
                    Accounts.Add(new AccountViewModel(acct));

                StatusMessage = $"Vault '{vault.VaultName}' updated by another user.";
            });
        };
    }

    // ──────────────────────────────────────────────
    // QR Scanning
    // ──────────────────────────────────────────────

    [RelayCommand]
    private async Task ScanQrFromScreen()
    {
        try
        {
            MinimizeWindow?.Invoke();
            await Task.Delay(300); // allow minimize animation to complete before screenshot

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

    /// <returns>true if added, false if skipped as a duplicate.</returns>
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

    // ──────────────────────────────────────────────
    // Export / Import
    // ──────────────────────────────────────────────

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

    // ──────────────────────────────────────────────
    // Drag-and-Drop Reorder
    // ──────────────────────────────────────────────

    [RelayCommand]
    private void MoveAccount(MoveArgs? args)
    {
        if (args == null) return;
        var (from, to) = args;
        if (from < 0 || from >= Accounts.Count || to < 0 || to >= Accounts.Count) return;

        // Only allow reorder within the same source (personal or same vault)
        var fromVault = Accounts[from].VaultName;
        var toVault = Accounts[to].VaultName;
        if (fromVault != toVault) return;

        if (fromVault == null)
        {
            // Personal accounts — use store
            _store.Move(from, to);
        }

        Accounts.Move(from, to);
    }

    // ──────────────────────────────────────────────
    // Windows Hello Toggle
    // ──────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleWindowsHello()
    {
        if (!WindowsHelloAvailable) return;

        if (!WindowsHelloEnabled)
        {
            if (await WindowsHelloService.VerifyAsync("Verify to enable Windows Hello lock"))
            {
                WindowsHelloEnabled = true;
                AppSettings.IsWindowsHelloEnabled = true;
                StatusMessage = "Windows Hello lock enabled.";
            }
        }
        else
        {
            WindowsHelloEnabled = false;
            AppSettings.IsWindowsHelloEnabled = false;
            StatusMessage = "Windows Hello lock disabled.";
        }
    }

    // ──────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────

    public void Shutdown()
    {
        foreach (var vault in _openVaults)
            vault.Dispose();
        _openVaults.Clear();
    }
}
