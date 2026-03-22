# WinAuth - Windows TOTP Authenticator

A lightweight Windows desktop 2FA authenticator built with WPF and .NET 8.

## Quick Start

```bash
cd WinAuth
dotnet restore
dotnet run
```

## Features

### TOTP Code Generation
Supports 6/8-digit codes, SHA1/SHA256/SHA512 algorithms, and configurable time periods. Codes refresh live with a countdown timer. Click any entry to copy the code to clipboard.

### QR Code Scanning
Three scan modes available from the hamburger menu:
- **Screen scan** — captures your full desktop and finds the QR code automatically. If it can't find one, falls back to a region selector overlay where you draw a box around the QR code.
- **File scan** — open any PNG/JPG/BMP image containing a QR code.
- **Clipboard scan** — if you've screenshot a QR code to your clipboard, scan it directly.

### Encrypted Export / Import
Backup your accounts to a `.winauth` file encrypted with AES-256-GCM (PBKDF2-SHA256 key derivation, 600k iterations). Password-protected with confirmation. Import restores accounts and re-encrypts secrets under your local DPAPI profile.

### Shared Team Vaults
For team-shared service accounts that need 2FA. Create a `.winauth` vault file, put it on a network share / OneDrive / SharePoint, and share the password with your team via a secure channel. Each team member opens the vault in their local WinAuth — codes generate locally, secrets never transit the network in plaintext.

**How it works:**
- Vault files use the same AES-256-GCM encryption as backups
- Secrets are decrypted into memory on open, never written to DPAPI (so they're not bound to one user's Windows profile)
- A FileSystemWatcher detects when a teammate adds/removes accounts — your view auto-refreshes
- File-level locking prevents concurrent write corruption
- Each account entry shows a source badge (🔒 Personal or 🔗 vault-name) so you always know where a code comes from
- The "Add to" dropdown in the add panel lets you route new accounts to any open vault

**Team workflow:**
1. One person creates the vault: Menu → Create Shared Vault
2. Share the `.winauth` file via OneDrive/SharePoint/network share
3. Share the vault password via a secure channel (not the same share)
4. Each team member: Menu → Open Shared Vault → enter password
5. Anyone can add/remove accounts — changes propagate to all users

### System Tray Mode
Minimizing or closing the window sends WinAuth to the system tray. Double-click the tray icon or use the global hotkey to restore. Right-click the tray icon for Show/Exit.

### Global Hotkey
**Ctrl+Shift+A** toggles WinAuth visibility from anywhere. Works even when the app is minimized to tray.

### Drag-and-Drop Reordering
Grab the ≡ handle on any account entry and drag it to reorder. Sort order persists across sessions.

### Windows Hello Lock
Enable from the menu to require fingerprint/face/PIN verification before WinAuth shows your codes. Falls back gracefully if Windows Hello isn't available on your hardware.

## How to Add Accounts

**Option 1 — QR code scan** (recommended)
Open the menu (≡) and choose a scan method. Most services show a QR code during 2FA setup.

**Option 2 — otpauth:// URI**
Paste the full `otpauth://totp/...` URI from a "Can't scan?" link.

**Option 3 — Manual entry**
Enter the issuer, label, and Base32 secret key.

## How It Works

- **TOTP codes** generated via [Otp.NET](https://github.com/kspearrin/Otp.NET) (RFC 6238)
- **Secrets encrypted at rest** using Windows DPAPI (user-scoped) — bound to your Windows login
- **Backups encrypted** with AES-256-GCM + PBKDF2-SHA256 key derivation
- **Storage** at `%LOCALAPPDATA%\WinAuth\accounts.json`
- **Settings** at `%LOCALAPPDATA%\WinAuth\settings.json`
- **System tray** via [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon)
- **Global hotkey** via Win32 RegisterHotKey interop
- **QR scanning** via [ZXing.NET](https://github.com/micjahn/ZXing.Net) + GDI+ screen capture
- **Windows Hello** via WinRT UserConsentVerifier interop

## Project Structure

```
WinAuth/
├── Behaviors/
│   └── DragDropReorder.cs          # Attached behavior for ListBox drag-and-drop
├── Models/
│   ├── AuthAccount.cs              # Account data model (personal + shared vault)
│   └── ExportPayload.cs            # Encrypted backup/vault format
├── Services/
│   ├── AccountStore.cs             # JSON persistence + URI parsing + reorder
│   ├── AppSettings.cs              # Settings persistence + recent vault paths
│   ├── ExportImportService.cs      # AES-256-GCM export/import
│   ├── GlobalHotkeyService.cs      # Win32 hotkey registration
│   ├── QrScanner.cs                # Screen/file/clipboard QR code scanning
│   ├── SecretVault.cs              # DPAPI encryption wrapper
│   ├── SharedVaultService.cs       # Shared team vault (open/create/watch/edit)
│   ├── TotpGenerator.cs            # TOTP code generation
│   └── WindowsHelloService.cs      # Biometric authentication
├── ViewModels/
│   ├── AccountViewModel.cs         # Per-account display logic + source badge
│   └── MainViewModel.cs            # App orchestration + vault management
├── Views/
│   ├── PasswordDialog.xaml/.cs     # Export/import/vault password entry
│   └── ScreenCaptureOverlay.xaml/.cs # Region selection for QR scan
├── MainWindow.xaml/.cs             # Main UI + tray + hotkey lifecycle
├── App.xaml/.cs
└── WinAuth.csproj
```

## Requirements

- Windows 10 19041+ (for Windows Hello WinRT interop)
- .NET 8 SDK
- For Windows Hello: biometric hardware or Windows Hello PIN configured

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+Shift+A | Show/hide WinAuth (global) |
| Click entry | Copy code to clipboard |
| Esc (in region selector) | Cancel QR scan |

