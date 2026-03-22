# WardLock — Windows TOTP Authenticator

A lightweight Windows desktop 2FA authenticator built with WPF and .NET 10.

## Quick Start

```
dotnet restore
dotnet run
```

## Features

### TOTP Code Generation
Supports 6/8-digit codes, SHA1/SHA256/SHA512 algorithms, and configurable time periods. Codes refresh live with a countdown timer. Click any entry to copy the code to clipboard.

### QR Code Scanning
Three scan modes available from the menu:
- **Screen scan** — captures your full desktop and finds the QR code automatically. If it can't find one, falls back to a region selector overlay where you draw a box around the QR code.
- **File scan** — open any PNG/JPG/BMP image containing a QR code.
- **Clipboard scan** — if you've screenshot a QR code to your clipboard, scan it directly.

### Google Authenticator Migration
Import accounts directly from Google Authenticator's "Export accounts" QR codes. Scan the migration QR code using any of the three scan modes above.

### Encrypted Backup / Restore
Backup your accounts to a `.wardlock` file encrypted with AES-256-GCM (PBKDF2-SHA256 key derivation, 600k iterations). Password-protected with confirmation. Import restores accounts and re-encrypts secrets under your local DPAPI profile.

### Shared Team Vaults
For team-shared service accounts that need 2FA. Create a `.wardlock` vault file, put it on a network share / OneDrive / SharePoint, and share the password with your team via a secure channel. Each team member opens the vault locally — codes generate locally, secrets never transit the network in plaintext.

**How it works:**
- Vault files use the same AES-256-GCM encryption as backups
- Secrets are decrypted into memory on open, never written to DPAPI (so they're not bound to one user's Windows profile)
- A FileSystemWatcher detects when a teammate adds/removes accounts — your view auto-refreshes
- File-level locking prevents concurrent write corruption
- Each account shows a source badge (Personal or vault-name) so you always know where a code comes from
- The "Add to" dropdown lets you route new accounts to any open vault

**Team workflow:**
1. One person creates the vault: Menu → Create Shared Vault
2. Share the `.wardlock` file via OneDrive / SharePoint / network share
3. Share the vault password via a secure channel (not the same share)
4. Each team member: Menu → Open Shared Vault → enter password
5. Anyone can add/remove accounts — changes propagate to all users automatically

### System Tray Mode
Minimizing or closing the window sends WardLock to the system tray. Double-click the tray icon or use the global hotkey to restore. Right-click the tray icon for Show/Exit.

### Global Hotkey
**Ctrl+Shift+A** toggles WardLock visibility from anywhere, even when minimized to tray.

### Drag-and-Drop Reordering
Grab the ≡ handle on any account entry and drag it to reorder. Sort order persists across sessions.

### Windows Hello Lock
Enable from the menu to require fingerprint/face/PIN verification before WardLock shows your codes. Falls back gracefully if Windows Hello is not available on your hardware.

## How to Add Accounts

**Option 1 — QR code scan** (recommended)
Open the menu (≡) and choose a scan method. Most services show a QR code during 2FA setup.

**Option 2 — otpauth:// URI**
Paste the full `otpauth://totp/...` URI from a "Can't scan?" link.

**Option 3 — Manual entry**
Enter the issuer, label, and Base32 secret key.

**Option 4 — Google Authenticator migration**
In Google Authenticator: Transfer accounts → Export accounts → scan the displayed QR code with WardLock.

## How It Works

- **TOTP codes** generated via [Otp.NET](https://github.com/kspearrin/Otp.NET) (RFC 6238)
- **Secrets encrypted at rest** using Windows DPAPI (user-scoped) — bound to your Windows login
- **Backups/vaults encrypted** with AES-256-GCM + PBKDF2-SHA256 key derivation (600k iterations)
- **Storage** at `%LOCALAPPDATA%\WardLock\accounts.json`
- **Settings** at `%LOCALAPPDATA%\WardLock\settings.json`
- **System tray** via [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon)
- **Global hotkey** via Win32 RegisterHotKey interop
- **QR scanning** via [ZXing.NET](https://github.com/micjahn/ZXing.Net) + GDI+ screen capture
- **Windows Hello** via WinRT UserConsentVerifier interop

## Building from Source

### Requirements
- Windows 10 Build 19041+ (for Windows Hello WinRT interop)
- .NET 10 SDK

### Run locally
```
dotnet restore
dotnet build -c Release -r win-x64
dotnet run
```

### Build the MSIX installer
1. Run the signing setup once (requires elevated prompt):
   ```
   powershell -ExecutionPolicy Bypass -File setup-signing.ps1
   ```
2. In Visual Studio: right-click `WardLock_Installer` → Publish → Create App Packages
3. Install the `.msixbundle` from the output folder

## Project Structure

```
WardLock/
├── Behaviors/
│   └── DragDropReorder.cs          # ListBox drag-and-drop reordering
├── Models/
│   ├── AuthAccount.cs              # Account data model (personal + shared vault)
│   └── ExportPayload.cs            # Encrypted backup/vault format
├── Services/
│   ├── AccountStore.cs             # JSON persistence + URI parsing + reorder
│   ├── AppSettings.cs              # Settings persistence + recent vault paths
│   ├── ExportImportService.cs      # AES-256-GCM export/import
│   ├── GlobalHotkeyService.cs      # Win32 hotkey registration
│   ├── GoogleAuthMigrationDecoder.cs # Google Authenticator migration QR import
│   ├── OAuthService.cs             # OAuth/authorization flows
│   ├── PasswordLockService.cs      # Windows Hello lock orchestration
│   ├── QrScanner.cs                # Screen/file/clipboard QR code scanning
│   ├── SecretVault.cs              # DPAPI encryption wrapper
│   ├── SharedVaultService.cs       # Shared team vault (open/create/watch/edit)
│   ├── TotpGenerator.cs            # TOTP code generation
│   ├── VaultPasswordCache.cs       # In-memory vault password caching
│   └── WindowsHelloService.cs      # Biometric authentication via WinRT
├── ViewModels/
│   ├── AccountViewModel.cs         # Per-account display logic + source badge
│   ├── MainViewModel.cs            # App orchestration + vault management
│   ├── MainViewModel.Search.cs     # Search/filter logic
│   └── Services/
│       ├── QrCoordinator.cs        # QR scan coordination
│       └── SharedVaultCoordinator.cs # Shared vault coordination
├── Views/
│   ├── PasswordDialog.xaml/.cs     # Export/import/vault password entry
│   └── ScreenCaptureOverlay.xaml/.cs # Region selection overlay for QR scan
├── MainWindow.xaml/.cs             # Main UI + tray + hotkey lifecycle
├── App.xaml/.cs
└── WardLock.csproj
```

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+Shift+A | Show/hide WardLock (global) |
| Click entry | Copy code to clipboard |
| Esc (in region selector) | Cancel QR scan |
