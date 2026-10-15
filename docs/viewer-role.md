# Viewer role: two-key wrapping design (issue #3)

Status: **implemented** (vault format v2). This doc records the threat model,
the decision, and the alternatives considered.

## Goal

Vault members with the **viewer** role can obtain current codes but cannot
export or reveal the underlying TOTP seeds. Vault **admins** keep full access.
Everything stays serverless: a vault is still just an encrypted file on a
share.

## The impossibility statement (read this first)

A TOTP code is `Truncate(HMAC(seed, t))`. There is no time-limited delegation
in TOTP: any party that can compute *future* codes locally, forever, must hold
the seed. So of these three properties, a design can pick **two**:

1. serverless (no online authority),
2. viewers can always produce fresh codes on their own,
3. viewers never possess the seed.

The SaaS competitors (Authn8, ShareOTP, MultiMFA) pick 2+3 — their server
holds the seed and computes codes on demand. A "viewer flag" enforced only by
the client UI picks 1+2 — the seed sits in the viewer's memory and any
modified client (or a script over the vault file) extracts it; that is
advisory, not enforcement.

WardLock picks **1+3** and relaxes 2 into a bounded window: viewers hold
**precomputed codes** for a limited horizon, refreshed whenever an admin's
client touches the vault. A viewer can never derive a seed from codes (that
would require inverting HMAC-SHA1), and a compromised viewer leaks at most the
remaining horizon of codes — after which the attacker has nothing, versus
"clone the seed forever" today.

## Vault format v2

Two independent keys, both random 256-bit, never derived from each other:

- **K_master** — encrypts the full payload (seeds + metadata). Wrapped under
  `PBKDF2-SHA256(adminPassword, salt_a, 600k)` in the **admin slot**.
- **K_viewer** — encrypts the **viewer payload** (metadata + precomputed code
  windows, no seeds). Wrapped twice: under
  `PBKDF2-SHA256(viewerPassword, salt_v, 600k)` in the **viewer slot**, and
  under K_master (so admin clients can re-encrypt windows without knowing the
  viewer password).

```jsonc
{
  "version": "2.0",
  "horizonHours": 72,
  "admin":            { "salt", "nonce", "tag", "wrappedKey" },   // K_master ⟵ KDF(adminPw)
  "viewer":           { "salt", "nonce", "tag", "wrappedKey" },   // K_viewer ⟵ KDF(viewerPw)
  "viewerKeyForAdmin":{ "nonce", "tag", "wrappedKey" },           // K_viewer ⟵ K_master
  "payload":          { "nonce", "tag", "data" },                 // seeds+meta ⟵ K_master
  "viewerPayload":    { "nonce", "tag", "data", "generatedAt" }   // windows   ⟵ K_viewer
}
```

All encryption is AES-256-GCM with fresh random nonces per write. Opening
tries the admin slot first, then the viewer slot; whichever KDF unwraps a key
determines the session's role. Both fail ⇒ wrong password.

### Code windows

The viewer payload holds, per account: issuer, label, digits, period, encoder,
sort order, fill domain, approval flag, and a **window** — `startStep` (the
RFC 6238 timestep, starting one step in the past for clock skew) plus a single
fixed-width string of concatenated codes covering `horizonHours` (default 72,
chosen so a vault survives a weekend without any admin online; ~52 KB per
6-digit/30 s account). Lookup is `codes[(now/period − startStep) × width]`.
Steam codes precompute identically (width 5).

Windows regenerate on every admin save, on admin open when older than
`horizon/3` (24 h), and on a half-hourly background check while an admin has
the vault open. A vault whose windows lapse shows viewers a stale marker
instead of codes until any admin opens it — this staleness is the honest price
of pick 1+3 above.

### Role capabilities

| | admin | viewer |
|---|---|---|
| see / copy / auto-type / browser-fill current codes | ✔ | ✔ (within horizon) |
| add / remove accounts, change domain or approval flag | ✔ | ✖ in the admin `payload` — **cryptographically**: a viewer cannot produce a valid `payload` without K_master. ⚠ The *viewer* payload is sealed with K_viewer, which every viewer holds, so a viewer with write access can re-seal it (e.g. change another viewer's fill domain or approval flag) until the next admin save — see Honest limits |
| move a vault account to personal (seed extraction) | ✔ | ✖ — the seed isn't present in the viewer's process at all |
| set / rotate / remove the viewer password | ✔ | ✖ |
| audit log: append + view | ✔ | ✔ (appends are per-member accountability, wanted from viewers) |

### Rotation & revocation

"Set Viewer Password…" always generates a **fresh K_viewer**, so it doubles as
rotation: a departed viewer's password stops opening the vault immediately,
and the codes they may have hoarded age out with the horizon. Removing viewer
access drops the viewer slot and payload entirely, and the file saves back to
the v1 format.

### Compatibility & migration

- v1 vaults open exactly as before (single password = admin) and keep saving
  as v1 **until** a viewer password is first set — no forced upgrade.
- v2 requires all *members* to run a WardLock version that reads v2. Set the
  viewer password only after everyone has upgraded.
- **Share permissions:** saves write a temp file next to the vault and swap it
  in (`ReplaceFile`, which keeps the vault file's ACL and owner). Members who
  save (admins) therefore need *create* permission in the vault's folder and
  *delete* permission on the vault file, not just write access to the file.
  Viewers append to the `.log` sidecar, so they need write access there.
- **Mixed versions:** saves also touch the file's last-write time so that
  clients from before atomic saves still notice and reload; upgrading every
  member is still recommended.

## Honest limits (unchanged or accepted)

- **A viewer sees codes** — that's the role's purpose. A malicious viewer can
  use codes while authorized, and can hoard at most the current horizon.
  `horizonHours` and `viewerPayloadGeneratedAt` live in the unauthenticated
  file header, so clients clamp them on read (horizon ≤ the 72 h default; a
  future generation time is ignored) — a viewer editing the header can't make
  admins precompute a longer window.
- **Viewers can forge the viewer payload.** GCM under K_viewer proves only
  that *some* key holder wrote it, and every viewer holds K_viewer. A viewer
  with write access to the share can re-seal the viewer payload with altered
  metadata — fill `domain`, `requireApproval` — affecting other *viewers'*
  browser fills until an admin's next save rewrites it (≤ horizon/3). Codes
  can't be forged into validity, and admins (who read the K_master payload)
  are unaffected. The fix is an admin signature over the viewer payload
  (tracked as a follow-up issue).
- **Anyone with share write access can destroy the file** (delete/garbage).
  The admin payload's GCM tag makes tampering with it detectable; availability
  was never in the threat model of a file on a share. Saves are atomic
  (temp file + rename), so a crash or dropped connection mid-save leaves the
  previous version intact rather than a truncated file.
- **Concurrent admin edits are last-writer-wins.** Two admins saving within
  the watcher's debounce window can drop one change; there is no revision
  check yet (tracked as a follow-up issue).
- **The admin password is still shared** among admins; per-member admin
  identity remains keyed to Windows usernames in the audit log (see the audit
  trail threat model in the README).
- The viewer password is a shared team credential too; per-viewer
  individualized slots are a straightforward extension (N viewer slots
  wrapping the same K_viewer) left for when someone needs it.

## Alternatives rejected

- **Advisory flag, single password** — no cryptographic teeth; a modified
  client dumps seeds. Rejected as dishonest versus our own README standards.
- **Split payload, seeds admin-only, viewers compute nothing** — viewers
  would see account names but no codes; useless.
- **Derived per-timestep subkeys** — for HMAC-based OTP, "per-code material"
  *is* the code (or the HMAC output that yields it); this converges to
  precomputed windows with extra steps.
- **Online relay computing codes** — right answer for pick 2+3, but it is the
  issue's *push approval* work item, deliberately optional and separate; the
  vault must keep working with the relay down.
