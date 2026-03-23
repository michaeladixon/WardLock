# WardLock Privacy Policy

**Effective Date:** March 22, 2026
**Last Updated:** March 22, 2026

WardLock is a desktop TOTP authenticator for Windows. This privacy policy explains what data WardLock handles, how it is stored, and what rights you have as a user.

## Summary

WardLock does not collect, transmit, or share any personal data. All authentication secrets and settings are stored locally on your device, encrypted at rest, and never leave your machine unless you explicitly export them.

## Data Storage

WardLock stores the following data locally on your Windows device:

- **TOTP account records** (issuer name, account label, encrypted secret key, algorithm settings, sort order) in `%LOCALAPPDATA%\WardLock\accounts.json`.
- **Application settings** (window position, recent vault paths, preferences) in `%LOCALAPPDATA%\WardLock\settings.json`.
- **Cached vault passwords** (for shared vault auto-reconnect) in `%LOCALAPPDATA%\WardLock\vault-keys\`, encrypted with Windows DPAPI.

No data is stored in the cloud, on remote servers, or in any location outside your local device.

## Encryption

WardLock uses two encryption methods to protect your data:

- **Windows DPAPI (Data Protection API)** encrypts personal TOTP secrets at rest. DPAPI keys are scoped to your Windows user profile — your secrets cannot be decrypted by another user account or on another machine.
- **AES-256-GCM** with PBKDF2-SHA256 key derivation (600,000 iterations) encrypts exported backup files and shared vault files. These files are protected by a password you choose.

Secrets are decrypted into memory only when generating TOTP codes and are never written to disk in plaintext.

## Network Activity

WardLock does not make any network calls from its own code. The application does not include telemetry, analytics, crash reporting, advertising, or any form of data collection.

The only network activity associated with WardLock is the standard Microsoft Store update mechanism, which is managed entirely by Windows and is subject to Microsoft's own privacy policy. WardLock has no visibility into or control over this process.

## Screen Capture

WardLock includes a QR code scanning feature that can capture a screenshot of your display to locate a QR code. This image is processed entirely in memory on your local device, is never stored to disk, and is never transmitted anywhere. The captured image is discarded immediately after the QR code is decoded.

## Windows Hello

WardLock optionally uses Windows Hello (fingerprint, facial recognition, or PIN) to lock access to the application. All biometric processing is handled by the Windows operating system. WardLock does not access, store, or process biometric data. It receives only a pass/fail verification result from the operating system.

## Shared Vault Files

WardLock supports shared vault files (`.wardlock` files) that can be placed on network shares, OneDrive, SharePoint, or similar shared storage. These files are encrypted with AES-256-GCM before being written to disk. WardLock reads and writes only the vault file itself — it does not communicate with any server or cloud service. The choice of where to store the vault file, and with whom to share it, is entirely yours.

## Clipboard Access

When you click an account entry, WardLock copies the current TOTP code to your Windows clipboard. WardLock does not read clipboard contents except during the "Clipboard scan" feature, where it reads an image from the clipboard to scan for a QR code. No clipboard data is stored or transmitted.

## Data Sharing

WardLock does not share data with any third party. There are no analytics providers, advertising networks, tracking pixels, or external services of any kind integrated into the application.

## Data Retention and Deletion

All WardLock data is stored locally on your device. To delete all WardLock data, remove the following directory:

```
%LOCALAPPDATA%\WardLock\
```

Uninstalling WardLock through Windows Settings or the Microsoft Store will remove the application. The local data directory listed above may need to be deleted manually to ensure complete removal of all stored data.

## Children's Privacy

WardLock does not knowingly collect any information from anyone, including children under the age of 13.

## Changes to This Policy

If this privacy policy is updated, the revised version will be published at this same URL with an updated "Last Updated" date. Continued use of WardLock after changes are posted constitutes acceptance of the revised policy.

## Open Source

WardLock is open source. You can inspect the complete source code to verify every claim in this privacy policy:

https://github.com/michaeladixon/WardLock

## Contact

If you have questions about this privacy policy or WardLock's data handling practices, please open an issue on the GitHub repository:

https://github.com/michaeladixon/WardLock/issues
