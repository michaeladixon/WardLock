#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time setup: generates the .snk for assembly signing and a self-signed
    .pfx for MSIX package signing, then installs it so sideloading works.

.DESCRIPTION
    Run this once from an elevated PowerShell prompt in the repo root:
        powershell -ExecutionPolicy Bypass -File setup-signing.ps1

    It creates:
      - WardLock.snk                              (strong name key)
      - WardLock_Installer\WardLock_Signing.pfx   (MSIX signing cert)

    And installs the cert into:
      - Cert:\CurrentUser\My            (so MSBuild can sign with it)
      - Cert:\LocalMachine\TrustedPeople (so sideload installs work)
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot       = $PSScriptRoot
$snkPath        = Join-Path $repoRoot 'WardLock.snk'
$pfxPath        = Join-Path $repoRoot 'WardLock_Installer\WardLock_Signing.pfx'
$pfxPassword    = 'WardLock2026!'
$certSubject    = 'CN=WardLock'

# ---------- 1. Strong Name Key ------------------------------------------------

if (Test-Path $snkPath) {
    Write-Host "[OK] WardLock.snk already exists - skipping." -ForegroundColor Green
}
else {
    Write-Host "Generating strong name key..." -ForegroundColor Cyan

    $sn = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows" `
              -Recurse -Filter 'sn.exe' -ErrorAction SilentlyContinue |
          Sort-Object FullName -Descending |
          Select-Object -First 1

    if ($sn) {
        & $sn.FullName -k $snkPath
    }
    else {
        Write-Host "  sn.exe not found - generating via .NET RSA API..." -ForegroundColor Yellow
        $csp = New-Object System.Security.Cryptography.RSACryptoServiceProvider(2048)
        [System.IO.File]::WriteAllBytes($snkPath, $csp.ExportCspBlob($true))
        $csp.Dispose()
    }

    if (Test-Path $snkPath) {
        Write-Host "[OK] Created $snkPath" -ForegroundColor Green
    }
    else {
        Write-Error "Failed to create WardLock.snk"
    }
}

# ---------- 2. Self-Signed MSIX Signing Certificate ---------------------------

Write-Host ""
Write-Host "Generating self-signed certificate for MSIX package signing..." -ForegroundColor Cyan
Write-Host "  Subject: $certSubject (must match Package.appxmanifest Publisher)" -ForegroundColor DarkGray

# Remove any old certs with same subject to keep things clean
Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $certSubject } |
    ForEach-Object {
        Write-Host "  Removing old cert: $($_.Thumbprint)" -ForegroundColor Yellow
        Remove-Item $_.PSPath -Force
    }

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $certSubject `
    -KeyUsage DigitalSignature `
    -FriendlyName 'WardLock MSIX Signing (Dev)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
    -NotAfter (Get-Date).AddYears(5)

Write-Host "[OK] Certificate created in CurrentUser\My" -ForegroundColor Green
Write-Host "     Thumbprint: $($cert.Thumbprint)" -ForegroundColor White
Write-Host "     Expires:    $($cert.NotAfter.ToString('yyyy-MM-dd'))" -ForegroundColor DarkGray

# Export to .pfx for the WAP project
$securePw = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePw | Out-Null
Write-Host "[OK] Exported to $pfxPath" -ForegroundColor Green

# Install to TrustedPeople so sideload works without manual cert install
$cerPath = Join-Path $env:TEMP 'WardLock_Signing.cer'
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Remove-Item $cerPath -Force
Write-Host "[OK] Installed to LocalMachine\TrustedPeople (sideloading will work)" -ForegroundColor Green

# ---------- 3. Update wapproj with new thumbprint ----------------------------

$waprojPath = Join-Path $repoRoot 'WardLock_Installer\WardLock_Installer.wapproj'
if (Test-Path $waprojPath) {
    $content = Get-Content $waprojPath -Raw
    $thumbTag = '<PackageCertificateThumbprint>'
    $newTag   = $thumbTag + $cert.Thumbprint + '</PackageCertificateThumbprint>'

    if ($content -match 'PackageCertificateThumbprint') {
        $content = $content -replace '<PackageCertificateThumbprint>[^<]*</PackageCertificateThumbprint>', $newTag
        Set-Content $waprojPath -Value $content -Encoding UTF8
        Write-Host "[OK] Updated PackageCertificateThumbprint in wapproj" -ForegroundColor Green
    }
    else {
        Write-Host "[WARN] No PackageCertificateThumbprint found in wapproj. Add manually:" -ForegroundColor Yellow
        Write-Host "       $newTag" -ForegroundColor White
    }
}

# ---------- Summary -----------------------------------------------------------

Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Cyan
Write-Host "Thumbprint:  $($cert.Thumbprint)" -ForegroundColor White
Write-Host "SNK:         $snkPath" -ForegroundColor White
Write-Host "PFX:         $pfxPath" -ForegroundColor White
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Rebuild:  dotnet build -c Release -r win-x64" -ForegroundColor White
Write-Host "  2. Publish:  Right-click WardLock_Installer > Publish > Create App Packages" -ForegroundColor White
Write-Host "  3. Install:  Run the .msixbundle from the output folder" -ForegroundColor White
Write-Host ""
Write-Host "The .snk and .pfx are gitignored - do NOT commit them." -ForegroundColor Yellow
