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

Steam Guard entries migrated from other authenticators are supported: URIs using `otpauth://steam/...` (Aegis exports) or `encoder=steam` (KeePassXC exports) import directly and render Steam's 5-character codes. Note that Steam offers no official way to obtain your shared secret — WardLock can only import one you already have from another app's export.

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

### Vault Audit Trail
Every shared vault keeps a tamper-evident audit log in a sidecar file next to the vault (`team-vault.wardlock.log`): vault created/opened, account added/removed, fill-domain changes, and every code access (copy, hotkey auto-type, browser fill) — stamped with the acting member's Windows username and UTC time. Entries are hash-chained (each record embeds the previous record's SHA-256), so any edit, deletion, or reordering breaks the chain and is flagged with a tamper warning — no server needed. View it from the menu (📜 next to each open vault) and export to CSV for compliance.

**Threat model — read this before relying on it:** the log is written by cooperating WardLock clients. The hash chain makes silent *modification* of history evident; it does not make the log unforgeable. A member with write access to the share can delete the log wholesale or truncate its tail (truncation is only evident against an expected entry count or a retained copy — for compliance, export CSV snapshots periodically). Identity comes from the Windows username, which a hostile member controls on their own machine. In short: it's an honest-participant accountability trail, comparable to what team-2FA SaaS products offer, not a cryptographic guarantee against a malicious insider.

### System Tray Mode
Minimizing or closing the window sends WardLock to the system tray. Double-click the tray icon or use the global hotkey to restore. Right-click the tray icon for Show/Exit.

### Global Hotkeys
**Ctrl+Shift+A** toggles WardLock visibility from anywhere, even when minimized to tray.

**Ctrl+Shift+T** types the current code into the focused field — no copy/paste, no phone. WardLock matches the account from the focused window's title (e.g. a browser tab titled "Sign in to GitHub" matches your GitHub account); if the match is ambiguous, a small picker appears at the cursor. Codes are never typed while the vault is locked.

### Browser Extension (domain-verified fill)
A Chrome/Edge extension delivers codes with one click — and **only on the real site**. Each account can store a fill domain (right-click → Set Fill Domain); the extension only offers and fills a code when the page's hostname equals that domain or is a subdomain of it, matched at label boundaries so `github.com.evil.com` can never receive your GitHub code. Communication is 100% local via native messaging (WardLock.exe doubles as the host process, relaying to the running app over a per-user named pipe). The app validates the calling extension's origin on every connection and refuses all requests while locked.

Setup: menu (≡) → **Enable Browser Integration**, then load the [`BrowserExtension`](BrowserExtension/README.md) folder unpacked in `chrome://extensions`. Not compatible with MSIX-installed builds yet (the browser can't launch executables inside `WindowsApps`) — use a loose build for browser integration.

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
- **Browser fill** via Chrome native messaging (stdio proxy → named pipe, `CurrentUserOnly`)
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

### Build the MSIX installer (sideload)
```powershell
.\build-msix.ps1
```
This handles everything: creates a self-signed cert, generates visual assets, publishes the app, assembles the MSIX with `MakeAppx.exe`, and signs it with `SignTool.exe`. On first run it installs the cert to `TrustedPeople` (requires an elevated prompt, or run without elevation and sideload manually).

Install the output:
```powershell
Add-AppxPackage -Path .\WardLock_1.0.0.0.msix
```

Common flags:
| Flag | Purpose |
|---|---|
| `-Version "1.2.0.0"` | Set the package version |
| `-SkipCert` | Reuse an existing cert instead of creating one |
| `-SkipAssets` | Skip icon generation if `Images\` is already populated |
| `-RunWack` | Run the Windows App Certification Kit after build |

**Requirements:** Windows 10/11 SDK (for `MakeAppx.exe` and `SignTool.exe`)

### Build for Microsoft Store submission
```powershell
.\build-msix.ps1 -Store -Version "1.0.0.0"
```
Stamps the Partner Center identity into the manifest and skips local signing (the Store re-signs on upload). Upload the resulting `.msix` to Partner Center → Your App → Packages.

## Project Structure

```
WardLock/
├── Behaviors/
│   └── DragDropReorder.cs          # ListBox drag-and-drop reordering
├── BrowserExtension/               # MV3 Chrome/Edge extension (load unpacked)
├── Models/
│   ├── AuthAccount.cs              # Account data model (personal + shared vault)
│   └── ExportPayload.cs            # Encrypted backup/vault format
├── Services/
│   ├── AccountStore.cs             # JSON persistence + URI parsing + reorder
│   ├── AppSettings.cs              # Settings persistence + recent vault paths
│   ├── AutoTypeService.cs          # Foreground-window detection + SendInput typing
│   ├── BrowserBridge/              # Native messaging: framing, proxy, pipe server, installer
│   ├── DomainMatcher.cs            # Label-anchored registrable-domain matching
│   ├── ExportImportService.cs      # AES-256-GCM export/import
│   ├── GlobalHotkeyService.cs      # Win32 hotkey registration
│   ├── GoogleAuthMigrationDecoder.cs # Google Authenticator migration QR import
│   ├── OAuthService.cs             # OAuth/authorization flows
│   ├── PasswordLockService.cs      # Windows Hello lock orchestration
│   ├── QrScanner.cs                # Screen/file/clipboard QR code scanning
│   ├── SecretVault.cs              # DPAPI encryption wrapper
│   ├── SharedVaultService.cs       # Shared team vault (open/create/watch/edit)
│   ├── TotpGenerator.cs            # TOTP code generation
│   ├── VaultAuditLog.cs            # Hash-chained tamper-evident vault audit trail
│   ├── VaultPasswordCache.cs       # In-memory vault password caching
│   └── WindowsHelloService.cs      # Biometric authentication via WinRT
├── ViewModels/
│   ├── AccountViewModel.cs         # Per-account display logic + source badge
│   ├── MainViewModel.cs            # App orchestration + vault management
│   ├── MainViewModel.AutoType.cs   # Window-title matching + auto-type flow
│   ├── MainViewModel.BrowserBridge.cs # Extension request handling + fill domains
│   ├── MainViewModel.Search.cs     # Search/filter logic
│   └── Services/
│       ├── QrCoordinator.cs        # QR scan coordination
│       └── SharedVaultCoordinator.cs # Shared vault coordination
├── Views/
│   ├── AuditLogWindow.xaml/.cs     # Vault audit trail viewer + CSV export
│   ├── AutoTypePickerWindow.xaml/.cs # Account picker popup for auto-type
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
| Ctrl+Shift+T | Auto-type current code into focused field (global) |
| Click entry | Copy code to clipboard |
| Esc (in region selector) | Cancel QR scan |
