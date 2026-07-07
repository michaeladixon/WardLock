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

### Number-matched approval

Accounts flagged **Require Approval to Fill** in WardLock (and *all* accounts
during the first 24 h after a browser profile first talks to WardLock) don't
fill immediately. Instead the popup shows a 2-digit number; type that number
into the WardLock window to release the code. The fill completes automatically
once approved — even if the popup closed when WardLock took focus — and the
approval is one-shot, expires after 60 s, and is denied after 3 wrong entries.

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
- **Out-of-band approval:** for approval-required fills, the 2-digit number is
  shown in the browser but typed into the WardLock window — a surface the page
  and the extension don't control. A spoofed requester can't approve itself,
  and there is no "Allow" button to click reflexively. The per-profile client
  ID that scopes the 24 h probation is self-asserted, so treat it as UX
  hardening; the domain check, lock state, and the out-of-band number entry are
  the real security boundaries.
