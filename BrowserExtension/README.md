# WardLock Browser Extension

One-click, domain-verified TOTP fill from the WardLock desktop app. Everything is
local — the extension talks to WardLock.exe over Chrome/Edge native messaging;
no cloud, no phone, no account.

## Setup

1. In WardLock: menu (≡) → **Enable Browser Integration**. This registers the
   native messaging host for Chrome and Edge (per-user, no admin needed).
2. In the browser: `chrome://extensions` (or `edge://extensions`) → enable
   **Developer mode** → **Load unpacked** → select this `BrowserExtension` folder.
   The `key` pinned in `manifest.json` gives the extension the stable ID
   `hcbclfghekjpdgnbfnmfeaamigencjjf`, which is what WardLock's host manifest
   authorizes — loading it unpacked from any path works.
3. In WardLock: right-click an account → **Set Fill Domain…** → enter the site's
   registrable domain (e.g. `github.com`).

## Usage

On a login page, click the WardLock toolbar icon. Accounts whose fill domain
matches the page are listed; click one and the code is filled into the OTP field
(clipboard fallback if no field is found).

## Troubleshooting

- **"WardLock is running as administrator…"** — the browser runs at medium
  integrity and Windows blocks it from reaching an elevated process's pipe.
  This happens when WardLock is launched from an elevated Visual Studio (F5)
  or an admin terminal. Restart WardLock normally, or run Visual Studio
  non-elevated.
- **"WardLock isn't running"** — the extension gets codes from the live app;
  start WardLock (it can sit in the tray).
- **"…host isn't registered"** — click *Enable Browser Integration* in the
  WardLock menu, then reopen the popup. Re-run it any time the exe moves
  (e.g. after switching Debug/Release build paths).

## Security model

- **Domain-verified:** WardLock only releases a code when the page's hostname
  equals the account's stored domain or is a subdomain of it (label-anchored —
  `github.com.evil.com` never matches `github.com`). A lookalike domain never
  receives a code.
- **Lock-state enforced:** a locked vault answers every request with `locked`.
- **Origin-checked twice:** the browser only launches the host for the
  `allowed_origins` in the host manifest, and WardLock re-validates the calling
  origin on every connection.
- **Accounts opt in:** accounts without a fill domain are invisible to the browser.
