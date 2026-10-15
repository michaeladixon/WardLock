# Releasing WardLock

`main` is the development branch — every feature merges there. A release is a
branch + tag, and the tag drives everything else.

## Continuous integration

`.github/workflows/ci.yml` runs on every pull request and every push to `main`:
restore → build with warnings as errors → unit tests (`tests/WardLock.Tests`)
→ browser extensions packaged for Chromium and Firefox and the Firefox build
checked with Mozilla's `addons-linter` → an unsigned Store-identity MSIX pack
(catches manifest/packaging breakage on the PR, not on the release tag) → the
loose win-x64 build and extension zips uploaded as artifacts for testers. Dependabot (`.github/dependabot.yml`) opens weekly
grouped PRs for NuGet packages and GitHub Actions.

## Cutting a release

```powershell
git checkout main && git pull
git checkout -b release/1.1.0
# (optional: release-only stabilization commits go here, merged back to main after)
git tag v1.1.0
git push origin release/1.1.0 v1.1.0
```

Pushing the tag triggers `.github/workflows/release.yml`:

1. **build** — refuses tags whose commit isn't on a `release/*` branch, runs
   the unit tests, then builds the release artifacts from the tag (version `vX.Y.Z` → MSIX `X.Y.Z.0`;
   the Store requires revision `0`, so tags are three-part only):
   - `WardLock_X.Y.Z.0_store.msix` — unsigned Store submission package
   - `WardLock_X.Y.Z.0_sideload.msix` — self-signed installer (only when the
     signing secrets are configured, see below)
   - `WardLock_X.Y.Z.0_win-x64.zip` — loose build, the install path for
     browser-extension users (MSIX installs can't host native messaging)
   - `WardLock_X.Y.Z.0-extension-chromium.zip` / `-extension-firefox.zip` —
     the browser extension (the Firefox build is unsigned; see
     `BrowserExtension/README.md`)
2. **release** — creates the GitHub Release on the tag with all artifacts,
   a `SHA256SUMS.txt`, and auto-generated notes. This is the versioned
   installer history.
3. **store** — waits for approval on the `microsoft-store` environment, runs a
   credential preflight (below), then publishes the store package to Partner
   Center with the Microsoft Store Developer CLI (`msstore publish`). Store
   certification (hours to a day or two) still happens on Microsoft's side
   before the update goes live.

### Store preflight — reading a failure

`msstore reconfigure` only ever says *"Really failed to auth"*. The preflight
step asks Entra ID for a token and calls the submission API itself, so the job
fails with the actual cause:

| Preflight says | Fix |
|---|---|
| Entra code `7000215` | `AZURE_AD_APPLICATION_SECRET` holds the wrong value — use the secret **Value** (shown once at creation), not the Secret ID |
| Entra code `7000222` | The client secret expired — create a new one |
| Entra code `700016` | Client ID not in that tenant — check `AZURE_AD_APPLICATION_CLIENT_ID` / `AZURE_AD_TENANT_ID` |
| Entra code `90002` / `900023` | Tenant ID wrong or malformed |
| HTTP 401 / 403 from the API | Token is fine, but the Entra app isn't added in Partner Center with the **Manager** role, or the tenant isn't the one associated with the Partner Center account |
| HTTP 404 from the API | `STORE_PRODUCT_ID` isn't an app in this seller account |
| "no published submission yet" | The first submission must be completed manually in Partner Center |

Hotfixes: commit on the existing `release/x.y.z` branch, tag the next patch
version (`v1.1.1`), push — then merge the release branch back to `main`.

## One-time setup

### Partner Center API access (store job)

1. In [Partner Center](https://partner.microsoft.com/) → Account settings →
   User management → **Microsoft Entra applications**: add an app registration
   from the tenant and give it the **Manager** role. Use a dedicated app
   registration, not the WNS relay one, so the secrets stay independently
   revocable.
2. Repo → Settings → Secrets and variables → Actions, add:

   | Secret | Value |
   |---|---|
   | `AZURE_AD_TENANT_ID` | Entra tenant ID |
   | `AZURE_AD_APPLICATION_CLIENT_ID` | App registration's Application (client) ID |
   | `AZURE_AD_APPLICATION_SECRET` | Client secret for that registration |
   | `SELLER_ID` | Partner Center → Account settings → Identifiers → Seller ID |
   | `STORE_PRODUCT_ID` | The app's Store ID (Partner Center → Product identity, `9N…`) |

3. Repo → Settings → Environments → create **`microsoft-store`** and add
   yourself as a required reviewer. That's the approval gate: tags always
   build, but nothing reaches the Store until you approve the run.

Prerequisites Microsoft lists for CI publishing
([docs](https://learn.microsoft.com/windows/apps/publish/msstore-dev-cli/github-actions)):
the app must **already be published and live** in the Store (CI publishes
updates, not the first submission), and automated updates currently support
**free products only** — revisit if a paid listing ever ships.

The secrets live on the `microsoft-store` **environment** (not the repo), so
only the approved store job can read them.

### Sideload signing (optional but recommended)

Export the existing signing cert and store it in secrets:

```powershell
$pw = Read-Host -AsSecureString "PFX password"
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=WardLock-Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1
Export-PfxCertificate -Cert $cert -FilePath wardlock-sideload.pfx -Password $pw
[Convert]::ToBase64String([IO.File]::ReadAllBytes('wardlock-sideload.pfx')) | Set-Clipboard
```

Add `SIDELOAD_CERT_PFX_BASE64` (clipboard contents) and
`SIDELOAD_CERT_PASSWORD`. Without these the workflow still succeeds — it just
skips the sideload package. With them, the package is signed and RFC 3161
timestamped (DigiCert by default, `-TimestampUrl` on `build-msix.ps1`), so it
stays installable after the certificate expires; a signing failure fails the
build instead of shipping an unsigned `_sideload.msix`. Upgrading to
[Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)
later removes the "trust this certificate" install step for sideloaders.
