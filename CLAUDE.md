# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

WardLock is a privacy-first WPF (.NET 10) TOTP authenticator for Windows 10/11. It generates one-time codes locally and delivers them by global hotkey (auto-type via `SendInput`) or a browser extension. Everything is local: no cloud, no telemetry. Personal secrets are DPAPI-encrypted at rest; backups and shared team vaults use AES-256-GCM with PBKDF2-SHA256 (600,000 iterations).

## Build / run

Requirements: Windows 10 build 19041+, .NET 10 SDK.

```bash
dotnet restore
dotnet build -c Release -r win-x64
dotnet run
```

MSIX packaging (uses `dotnet publish` + `MakeAppx.exe` + `SignTool.exe`; WPF can't do single-project MSIX):

```powershell
.\build-msix.ps1                                   # self-signed sideload build
.\build-msix.ps1 -Store -Version "1.0.0.0"         # Store submission (Microsoft re-signs)
.\build-msix.ps1 -RunWack -SkipCert -SkipAssets    # run cert kit only
Add-AppxPackage -Path .\WardLock_1.0.0.0.msix      # install the sideload package
```

There is **no test project** and **no CI** in this repo — validation is manual/integration.

## Codebase index

This repo is served by the `codebase-index` MCP server, and a hook blocks raw Read/Grep of indexed `.cs` files. Use `find_symbol` / `get_function_source` / `search_codebase` / `get_structure_summary` first; `Read` is only for unindexed files (`.csproj`, `.ps1`, `.md`, `.json`). Run `reindex()` after edits.

## Architecture

MVVM via `CommunityToolkit.Mvvm` (`ObservableObject`, `[RelayCommand]`). No DI container — services are constructed directly, mostly from `MainViewModel`.

- **ViewModels/** — `MainViewModel` (~1000 LOC) is the orchestrator, split into partials by concern: `MainViewModel.cs` (state, lock flows, account/vault management), `.AutoType.cs` (foreground-window title matching + typing), `.BrowserBridge.cs` (extension request handling), `.Search.cs` (filtering + idle auto-lock). `AccountViewModel` wraps a single account for display.
- **Services/** — capability-oriented, largely stateless statics or per-vault instances:
  - Crypto/storage: `SecretVault` (DPAPI), `AccountStore` (accounts.json + `otpauth://` parsing), `ExportImportService` (AES-256-GCM backups), `SharedVaultService` (encrypted team vault on a network share, live-synced via `FileSystemWatcher`), `VaultAuditLog` (append-only, SHA-256 hash-chained, tamper-evident), `VaultPasswordCache` (DPAPI-cached vault keys).
  - Codes: `TotpGenerator` (RFC 6238 + Steam), `QrScanner` (ZXing), `GoogleAuthMigrationDecoder`.
  - Delivery/auth: `AutoTypeService` (`SendInput`), `GlobalHotkeyService` (Win32 `RegisterHotKey`), `WindowsHelloService` (WinRT biometric), `OAuthService` (Google/Microsoft/Facebook PKCE unlock), `PasswordLockService` (PBKDF2 app lock), `DomainMatcher` (label-anchored, phishing-resistant domain match).
- **Views/** — WPF dialogs (audit log viewer, auto-type picker, password/input prompts, screen-capture QR overlay).
- **BrowserExtension/** — MV3 Chrome/Edge extension (native-messaging client).

### Browser bridge (two-process design — read this before touching it)

The same `WardLock.exe` plays two roles:

1. **Native-messaging proxy (headless).** When Chrome/Edge launch the exe as the extension's native host, they pass the extension origin as an argument. `App.OnStartup` (`App.xaml.cs`) detects any arg starting with `chrome-extension://` and, if present, runs `NativeMessagingProxy.Run(origin)` — a stdio↔named-pipe relay with **no UI** — then shuts down. Otherwise it shows `MainWindow`.
2. **App-side bridge server.** The running GUI instance hosts `BrowserBridgeServer` on a named pipe. The proxy connects as a client, performs a `hello`/origin handshake, and forwards framed JSON.

Key facts:
- Pipe name is the constant `NativeMessagingProxy.PipeName` = `"WardLock.BrowserBridge"`. Per-user isolation comes from `PipeOptions.CurrentUserOnly`, **not** from any SID embedded in the name.
- The proxy holds no secrets and makes no decisions — lock state, origin validation, and domain matching are all enforced app-side.
- If the app runs at higher integrity than the browser (e.g. launched elevated / from an admin Visual Studio), the proxy hits `UnauthorizedAccessException` on the pipe and reports `app-elevated`. Launch WardLock non-elevated when testing browser fill.
- Browser integration does **not** work from an MSIX-installed build (browsers can't launch executables under `WindowsApps`). Use a loose `dotnet` build for extension testing.

### Global hotkeys

`GlobalHotkeyService.Register` sets two `Ctrl+Shift` hotkeys (with `MOD_NOREPEAT`): `A` toggles the window, `T` auto-types the code matching the foreground window.

### Storage locations

Under `%LOCALAPPDATA%\WardLock\`: `accounts.json` (DPAPI-encrypted secrets), `settings.json` (window state, remembered vault paths, preferences), and `vault-keys\` (DPAPI-cached vault passwords).

## Project specifics

- Target framework `net10.0-windows10.0.19041.0`, `WinExe`, nullable + implicit usings enabled.
- Assemblies are **not** strong-name signed. Strong-naming was removed (the `WardLock.snk` private key was never committed, and nothing consumes the assembly as a library). Package trust comes from the MSIX signature instead — a sideload Authenticode cert (`SIDELOAD_CERT_PFX_BASE64` secret) or Microsoft's re-signing for Store submissions.
- `RuntimeIdentifiers` is `win-x64`; there is also a `Release|x86` platform config.
- Packages: `Otp.NET`, `CommunityToolkit.Mvvm`, `ZXing.Net.Bindings.Windows.Compatibility`, `Hardcodet.NotifyIcon.Wpf`.
