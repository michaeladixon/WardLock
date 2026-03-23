<#
.SYNOPSIS
    WardLock MSIX Build and Certification Script

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER SkipCert
    Skip certificate creation if you already have one installed.

.PARAMETER SkipAssets
    Skip visual asset generation if Images folder is already populated.

.PARAMETER RunWack
    Run Windows App Certification Kit after build.

.PARAMETER CertSubject
    Certificate subject/publisher identity. Must match Package.appxmanifest Publisher.
    Default: CN=WardLock-Dev

.PARAMETER Version
    Package version. Default: 1.0.0.0

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -RunWack
    .\build-msix.ps1 -Version "1.2.0.0" -RunWack -SkipCert -SkipAssets
#>

param(
    [string]$Configuration = 'Release',
    [switch]$SkipCert,
    [switch]$SkipAssets,
    [switch]$RunWack,
    [string]$CertSubject = 'CN=WardLock-Dev',
    [string]$Version = '1.0.0.0'
)

$ErrorActionPreference = 'Stop'
$ProjectRoot   = $PSScriptRoot
$CsprojPath    = Join-Path $ProjectRoot 'WardLock.csproj'
$ManifestPath  = Join-Path $ProjectRoot 'Package.appxmanifest'
$ImagesDir     = Join-Path $ProjectRoot 'Images'

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  WardLock MSIX Build Pipeline' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# ------------------------------------------------------------------------------
# 1. Stamp version into Package.appxmanifest
# ------------------------------------------------------------------------------

if (-not (Test-Path $ManifestPath)) {
    Write-Error 'Package.appxmanifest not found. Add it to the project root.'
    exit 1
}

Write-Host '[1/6] Stamping version into manifest...' -ForegroundColor Yellow
$content = Get-Content $ManifestPath -Raw
$content = $content -replace 'Version="[\d\.]+"', ('Version="' + $Version + '"')
$content = $content -replace 'Publisher="[^"]*"', ('Publisher="' + $CertSubject + '"')
Set-Content $ManifestPath -Value $content -NoNewline
Write-Host ('  Version: ' + $Version + ' | Publisher: ' + $CertSubject) -ForegroundColor DarkGray

# ------------------------------------------------------------------------------
# 2. Create or locate signing certificate
# ------------------------------------------------------------------------------

$Thumbprint = $null

if (-not $SkipCert) {
    Write-Host '[2/6] Setting up code-signing certificate...' -ForegroundColor Yellow

    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $CertSubject -and
        $_.NotAfter -gt (Get-Date) -and
        $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3'
    } | Sort-Object NotAfter -Descending | Select-Object -First 1

    if ($existing) {
        $Thumbprint = $existing.Thumbprint
        $expiryStr = $existing.NotAfter.ToString('yyyy-MM-dd')
        Write-Host ('  Found existing cert: ' + $Thumbprint + ' expires ' + $expiryStr) -ForegroundColor DarkGray
    }
    else {
        Write-Host '  Creating new self-signed certificate...' -ForegroundColor DarkGray
        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $CertSubject `
            -KeyUsage DigitalSignature `
            -FriendlyName 'WardLock MSIX Signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -NotAfter (Get-Date).AddYears(3) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

        $Thumbprint = $cert.Thumbprint
        Write-Host ('  Created cert: ' + $Thumbprint) -ForegroundColor DarkGray

        $cerFile = Join-Path $ProjectRoot 'wardlock-dev.cer'
        Export-Certificate -Cert $cert -FilePath $cerFile | Out-Null

        try {
            Import-Certificate -FilePath $cerFile -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
            Write-Host '  Installed to TrustedPeople for sideloading' -ForegroundColor DarkGray
        }
        catch {
            Write-Warning '  Could not install to TrustedPeople. Run as admin for sideloading.'
        }
        finally {
            Remove-Item $cerFile -ErrorAction SilentlyContinue
        }
    }
}
else {
    Write-Host '[2/6] Skipping certificate creation...' -ForegroundColor DarkGray

    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date)
    } | Select-Object -First 1

    if ($existing) {
        $Thumbprint = $existing.Thumbprint
    }
    else {
        Write-Error ('No certificate found for ' + $CertSubject + '. Run without -SkipCert first.')
        exit 1
    }
}

Write-Host ('  Thumbprint: ' + $Thumbprint) -ForegroundColor Green

# ------------------------------------------------------------------------------
# 3. Generate MSIX visual assets from shield polygon
# ------------------------------------------------------------------------------

if (-not $SkipAssets) {
    Write-Host '[3/6] Generating MSIX visual assets...' -ForegroundColor Yellow

    if (-not (Test-Path $ImagesDir)) { New-Item $ImagesDir -ItemType Directory | Out-Null }

    Add-Type -AssemblyName System.Drawing

    $assets = @(
        @{ Name = 'StoreLogo.png';          W = 50;  H = 50  }
        @{ Name = 'Square44x44Logo.png';    W = 44;  H = 44  }
        @{ Name = 'Square150x150Logo.png';  W = 150; H = 150 }
        @{ Name = 'Wide310x150Logo.png';    W = 310; H = 150 }
        @{ Name = 'Square310x310Logo.png';  W = 310; H = 310 }
        @{ Name = 'SmallTile.png';          W = 71;  H = 71  }
        @{ Name = 'SplashScreen.png';       W = 620; H = 300 }
    )

    foreach ($asset in $assets) {
        $assetName = $asset.Name
        $w = $asset.W
        $h = $asset.H
        $outPath = Join-Path $ImagesDir $assetName

        $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::FromArgb(255, 30, 30, 46))

        # Draw centered shield
        $iconSize = [Math]::Min($w, $h) * 0.7
        $s = $iconSize / 64.0
        $ox = ($w - $iconSize) / 2
        $oy = ($h - $iconSize) / 2

        $pts = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new($ox + 32*$s, $oy +  2*$s),
            [System.Drawing.PointF]::new($ox + 61*$s, $oy +  8*$s),
            [System.Drawing.PointF]::new($ox + 61*$s, $oy + 36*$s),
            [System.Drawing.PointF]::new($ox + 32*$s, $oy + 62*$s),
            [System.Drawing.PointF]::new($ox +  3*$s, $oy + 36*$s),
            [System.Drawing.PointF]::new($ox +  3*$s, $oy +  8*$s)
        )

        $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            [System.Drawing.RectangleF]::new($ox, $oy, $iconSize, $iconSize),
            [System.Drawing.Color]::FromArgb(255, 137, 180, 250),
            [System.Drawing.Color]::FromArgb(255,  56,  97, 190),
            [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)

        $g.FillPolygon($grad, $pts)
        $grad.Dispose()
        $g.Dispose()

        $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()

        Write-Host ('  ' + $assetName + ' [' + $w + 'x' + $h + ']') -ForegroundColor DarkGray
    }
}
else {
    Write-Host '[3/6] Skipping asset generation...' -ForegroundColor DarkGray
}

# ------------------------------------------------------------------------------
# 4. Patch csproj with MSIX properties (idempotent)
# ------------------------------------------------------------------------------

Write-Host '[4/6] Patching csproj for MSIX build...' -ForegroundColor Yellow

$csproj = Get-Content $CsprojPath -Raw

if ($csproj -notmatch 'WindowsPackageType') {
    $msixLines = @(
        '    <!-- MSIX Packaging - managed by build-msix.ps1 -->'
        '    <WindowsPackageType>MSIX</WindowsPackageType>'
        '    <AppxPackageSigningEnabled>true</AppxPackageSigningEnabled>'
        ('    <PackageCertificateThumbprint>' + $Thumbprint + '</PackageCertificateThumbprint>')
        '    <AppxBundle>Never</AppxBundle>'
        ('    <AppxPackageDir>bin\' + $Configuration + '\AppPackages\</AppxPackageDir>')
        '    <GenerateAppxPackageOnBuild>true</GenerateAppxPackageOnBuild>'
    )
    $msixBlock = [string]::Join("`n", $msixLines)
    $csproj = $csproj.Replace('</PropertyGroup>', $msixBlock + "`n  </PropertyGroup>")
    Set-Content $CsprojPath -Value $csproj -NoNewline
    Write-Host '  Injected MSIX properties into csproj' -ForegroundColor DarkGray
}
else {
    $csproj = $csproj -replace '<PackageCertificateThumbprint>[^<]*</PackageCertificateThumbprint>',
        ('<PackageCertificateThumbprint>' + $Thumbprint + '</PackageCertificateThumbprint>')
    Set-Content $CsprojPath -Value $csproj -NoNewline
    Write-Host '  Updated thumbprint in existing MSIX config' -ForegroundColor DarkGray
}

# Ensure Images are included as Content
if ($csproj -notmatch 'Images\\') {
    $imgLines = @(
        ''
        '  <ItemGroup>'
        '    <!-- MSIX visual assets -->'
        '    <Content Include="Images\*.png" CopyToOutputDirectory="PreserveNewest" />'
        '  </ItemGroup>'
    )
    $imgInsert = [string]::Join("`n", $imgLines)
    $csproj = Get-Content $CsprojPath -Raw
    $csproj = $csproj.Replace('</Project>', $imgInsert + "`n</Project>")
    Set-Content $CsprojPath -Value $csproj -NoNewline
    Write-Host '  Added Images content include' -ForegroundColor DarkGray
}

# ------------------------------------------------------------------------------
# 5. Build MSIX
# ------------------------------------------------------------------------------

Write-Host '[5/6] Building MSIX package...' -ForegroundColor Yellow
Write-Host ('  Configuration: ' + $Configuration) -ForegroundColor DarkGray

$buildArgs = @(
    'publish'
    $CsprojPath
    '-c'
    $Configuration
    '-r'
    'win-x64'
    '--self-contained'
    'true'
    '-p:Platform=x64'
    '-p:RuntimeIdentifierOverride=win-x64'
)

& dotnet @buildArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error ('Build failed with exit code ' + $LASTEXITCODE)
    exit $LASTEXITCODE
}

# Locate the output MSIX
$packageDir = Join-Path (Join-Path (Join-Path $ProjectRoot 'bin') $Configuration) 'AppPackages'
$msixFile   = Get-ChildItem $packageDir -Filter '*.msix' -Recurse -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $msixFile) {
    $msixFile = Get-ChildItem $packageDir -Filter '*.appx' -Recurse -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

# Fallback: Platform=x64 may output to bin\x64\Release\ instead of bin\Release\
if (-not $msixFile) {
    $binDir = Join-Path $ProjectRoot 'bin'
    $msixFile = Get-ChildItem $binDir -Filter '*.msix' -Recurse -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if (-not $msixFile) {
    $binDir = Join-Path $ProjectRoot 'bin'
    $msixFile = Get-ChildItem $binDir -Filter '*.appx' -Recurse -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if ($msixFile) {
    $sizeMB = [math]::Round($msixFile.Length / 1MB, 2)
    Write-Host ''
    Write-Host ('  MSIX output: ' + $msixFile.FullName) -ForegroundColor Green
    Write-Host ('  Size: ' + $sizeMB + ' MB') -ForegroundColor DarkGray
}
else {
    Write-Warning ('  Could not locate output package in ' + $packageDir)
    Write-Host ('  Check bin\' + $Configuration + '\ for the package output.') -ForegroundColor DarkGray
}

# ------------------------------------------------------------------------------
# 6. Run WACK (optional)
# ------------------------------------------------------------------------------

if ($RunWack) {
    Write-Host '[6/6] Running Windows App Certification Kit...' -ForegroundColor Yellow

    $wackPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'

    if (-not (Test-Path $wackPath)) {
        $wackSearch = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits') -Recurse -Filter 'appcert.exe' -ErrorAction SilentlyContinue |
                      Select-Object -First 1
        if ($wackSearch) {
            $wackPath = $wackSearch.FullName
        }
        else {
            Write-Warning '  WACK not found. Install the Windows SDK.'
            Write-Host '  You can run WACK manually later with appcert.exe' -ForegroundColor DarkGray
            $RunWack = $false
        }
    }

    if ($RunWack -and $msixFile) {
        $reportPath = Join-Path $ProjectRoot 'wack-report.xml'
        Write-Host ('  Validating: ' + $msixFile.Name) -ForegroundColor DarkGray
        Write-Host ('  Report: ' + $reportPath) -ForegroundColor DarkGray

        & $wackPath test -apptype desktop -setuppath $msixFile.FullName -reportoutputpath $reportPath

        if (Test-Path $reportPath) {
            $reportXml = [xml](Get-Content $reportPath)
            $overall   = $reportXml.REPORT.OVERALL_RESULT

            if ($overall -eq 'PASS') {
                Write-Host ''
                Write-Host '  WACK RESULT: PASS' -ForegroundColor Green
            }
            else {
                Write-Host ''
                Write-Host ('  WACK RESULT: ' + $overall) -ForegroundColor Red
                Write-Host ('  Review ' + $reportPath + ' for details.') -ForegroundColor Yellow

                $failures = $reportXml.SelectNodes("//TEST[@RESULT='FAIL']")
                foreach ($fail in $failures) {
                    Write-Host ('    FAIL: ' + $fail.NAME) -ForegroundColor Red
                }
            }
        }
    }
    elseif ($RunWack) {
        Write-Warning '  No MSIX package found to validate.'
    }
}
else {
    Write-Host '[6/6] Skipping WACK. Use -RunWack to enable.' -ForegroundColor DarkGray
}

# ------------------------------------------------------------------------------
# Summary
# ------------------------------------------------------------------------------

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  Build Complete' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ('  Certificate:  ' + $Thumbprint)
Write-Host ('  Publisher:    ' + $CertSubject)
Write-Host ('  Version:      ' + $Version)
Write-Host ('  Config:       ' + $Configuration)
if ($msixFile) {
    Write-Host ('  Package:      ' + $msixFile.FullName) -ForegroundColor Green
}
Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Yellow
Write-Host '    Sideload:     Add-AppxPackage -Path your-package.msix' -ForegroundColor DarkGray
Write-Host '    Store submit: Upload MSIX to Partner Center' -ForegroundColor DarkGray
Write-Host '    Run WACK:     .\build-msix.ps1 -RunWack -SkipCert -SkipAssets' -ForegroundColor DarkGray
Write-Host ''