<#
.SYNOPSIS
    WardLock MSIX Build and Certification Script.
    Uses dotnet publish + MakeAppx.exe + SignTool.exe because WPF does not
    support single-project MSIX packaging natively.

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER SkipCert
    Skip certificate creation if you already have one installed.

.PARAMETER SkipAssets
    Skip visual asset generation if Images folder is already populated.

.PARAMETER RunWack
    Run Windows App Certification Kit after build.

.PARAMETER CertSubject
    Certificate CN. Must match Package.appxmanifest Publisher.
    Default: CN=WardLock-Dev

.PARAMETER Version
    Package version. Default: 1.0.0.0

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -RunWack
    .\build-msix.ps1 -Version "1.2.0.0" -SkipCert -SkipAssets
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
$ProjectRoot  = $PSScriptRoot
$CsprojPath   = Join-Path $ProjectRoot 'WardLock.csproj'
$ManifestPath = Join-Path $ProjectRoot 'Package.appxmanifest'
$ImagesDir    = Join-Path $ProjectRoot 'Images'

# UTF-8 without BOM encoder - MakeAppx requires this
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host '  WardLock MSIX Build Pipeline' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

# ------------------------------------------------------------------------------
# Helper: find a tool in the Windows SDK
# ------------------------------------------------------------------------------
function Find-SdkTool {
    param([string]$ToolName)
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kitsRoot)) { return $null }
    $found = Get-ChildItem $kitsRoot -Recurse -Filter $ToolName -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match 'x64' } |
             Sort-Object FullName -Descending |
             Select-Object -First 1
    return $found.FullName
}

# Verify required SDK tools exist
$makeAppx  = Find-SdkTool 'makeappx.exe'
$signTool  = Find-SdkTool 'signtool.exe'

if (-not $makeAppx) {
    Write-Error 'makeappx.exe not found. Install the Windows 10/11 SDK.'
    exit 1
}
if (-not $signTool) {
    Write-Error 'signtool.exe not found. Install the Windows 10/11 SDK.'
    exit 1
}

Write-Host ('  makeappx: ' + $makeAppx) -ForegroundColor DarkGray
Write-Host ('  signtool: ' + $signTool) -ForegroundColor DarkGray

# ------------------------------------------------------------------------------
# 1. Stamp version and publisher into Package.appxmanifest (XML-safe)
# ------------------------------------------------------------------------------

if (-not (Test-Path $ManifestPath)) {
    Write-Error 'Package.appxmanifest not found. Add it to the project root.'
    exit 1
}

Write-Host '[1/7] Stamping version into manifest...' -ForegroundColor Yellow

# Use proper XML parsing to avoid corrupting the XML declaration or other attributes
$mfXml = New-Object System.Xml.XmlDocument
$mfXml.PreserveWhitespace = $true
$mfXml.Load($ManifestPath)

$nsMgr = New-Object System.Xml.XmlNamespaceManager($mfXml.NameTable)
$nsMgr.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

$identity = $mfXml.SelectSingleNode('//m:Identity', $nsMgr)
if ($identity) {
    $identity.SetAttribute('Version', $Version)
    $identity.SetAttribute('Publisher', $CertSubject)
}

# Save as UTF-8 without BOM
$writerSettings = New-Object System.Xml.XmlWriterSettings
$writerSettings.Encoding = $Utf8NoBom
$writerSettings.Indent = $true
$writerSettings.IndentChars = '  '
$writerSettings.OmitXmlDeclaration = $false

$stream = New-Object System.IO.StreamWriter($ManifestPath, $false, $Utf8NoBom)
$writer = [System.Xml.XmlWriter]::Create($stream, $writerSettings)
$mfXml.Save($writer)
$writer.Close()
$stream.Close()

Write-Host ('  Version: ' + $Version + ' | Publisher: ' + $CertSubject) -ForegroundColor DarkGray

# ------------------------------------------------------------------------------
# 2. Create or locate signing certificate
# ------------------------------------------------------------------------------

$Thumbprint = $null

if (-not $SkipCert) {
    Write-Host '[2/7] Setting up code-signing certificate...' -ForegroundColor Yellow

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
    Write-Host '[2/7] Skipping certificate creation...' -ForegroundColor DarkGray

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
# 3. Generate MSIX visual assets
# ------------------------------------------------------------------------------

if (-not $SkipAssets) {
    Write-Host '[3/7] Generating MSIX visual assets...' -ForegroundColor Yellow

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
    Write-Host '[3/7] Skipping asset generation...' -ForegroundColor DarkGray
}

# ------------------------------------------------------------------------------
# 4. Remove single-project MSIX properties from csproj (if present)
#    WPF does not support single-project MSIX; these cause silent no-output.
# ------------------------------------------------------------------------------

Write-Host '[4/7] Cleaning csproj of unsupported MSIX properties...' -ForegroundColor Yellow
$csproj = Get-Content $CsprojPath -Raw
$dirty = $false

$removePatterns = @(
    '<WindowsPackageType>MSIX</WindowsPackageType>'
    '<GenerateAppxPackageOnBuild>true</GenerateAppxPackageOnBuild>'
    '<AppxBundle>Never</AppxBundle>'
    '<AppxPackageSigningEnabled>true</AppxPackageSigningEnabled>'
    '<!-- MSIX Packaging - managed by build-msix.ps1 -->'
)

foreach ($pat in $removePatterns) {
    if ($csproj.Contains($pat)) {
        $csproj = $csproj.Replace($pat, '')
        $dirty = $true
    }
}

# Remove PackageCertificateThumbprint and AppxPackageDir lines
if ($csproj -match '<PackageCertificateThumbprint>') {
    $csproj = $csproj -replace '\s*<PackageCertificateThumbprint>[^<]*</PackageCertificateThumbprint>', ''
    $dirty = $true
}
if ($csproj -match '<AppxPackageDir>') {
    $csproj = $csproj -replace '\s*<AppxPackageDir>[^<]*</AppxPackageDir>', ''
    $dirty = $true
}

if ($dirty) {
    # Clean up empty lines left behind
    $csproj = ($csproj -split "`n" | Where-Object { $_.Trim() -ne '' }) -join "`n"
    [System.IO.File]::WriteAllText($CsprojPath, $csproj, $Utf8NoBom)
    Write-Host '  Removed unsupported single-project MSIX properties' -ForegroundColor DarkGray
}
else {
    Write-Host '  csproj is clean' -ForegroundColor DarkGray
}

# ------------------------------------------------------------------------------
# 5. dotnet publish (produces the layout folder)
# ------------------------------------------------------------------------------

Write-Host '[5/7] Publishing WardLock...' -ForegroundColor Yellow

$publishDir = Join-Path $ProjectRoot 'bin' | Join-Path -ChildPath 'msix-publish'

& dotnet publish $CsprojPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error ('dotnet publish failed with exit code ' + $LASTEXITCODE)
    exit $LASTEXITCODE
}

Write-Host ('  Published to: ' + $publishDir) -ForegroundColor DarkGray

# ------------------------------------------------------------------------------
# 6. Assemble MSIX with MakeAppx.exe
# ------------------------------------------------------------------------------

Write-Host '[6/7] Assembling MSIX package...' -ForegroundColor Yellow

# Copy manifest and assets into the publish layout
Copy-Item $ManifestPath (Join-Path $publishDir 'AppxManifest.xml') -Force
$destImages = Join-Path $publishDir 'Images'
if (-not (Test-Path $destImages)) { New-Item $destImages -ItemType Directory | Out-Null }
Copy-Item (Join-Path $ImagesDir '*') $destImages -Force

# Create a mapping file for MakeAppx
$mappingFile = Join-Path $ProjectRoot 'AppxMapping.ini'
$mappingLines = @('[Files]')

$pubFiles = Get-ChildItem $publishDir -Recurse -File
foreach ($f in $pubFiles) {
    $relativePath = $f.FullName.Substring($publishDir.Length).TrimStart('\')
    $line = '"' + $f.FullName + '"  "' + $relativePath + '"'
    $mappingLines += $line
}

[System.IO.File]::WriteAllText($mappingFile, ($mappingLines -join "`n"), $Utf8NoBom)

$msixOutput = Join-Path $ProjectRoot ('WardLock_' + $Version + '.msix')

# Remove old package if it exists
if (Test-Path $msixOutput) { Remove-Item $msixOutput -Force }

& $makeAppx pack /f $mappingFile /p $msixOutput /o

if ($LASTEXITCODE -ne 0) {
    Write-Error ('MakeAppx failed with exit code ' + $LASTEXITCODE)
    exit $LASTEXITCODE
}

# Clean up mapping file
Remove-Item $mappingFile -ErrorAction SilentlyContinue

# Sign the package
Write-Host '  Signing MSIX...' -ForegroundColor DarkGray
& $signTool sign /fd SHA256 /sha1 $Thumbprint /td SHA256 $msixOutput

if ($LASTEXITCODE -ne 0) {
    Write-Warning '  SignTool failed. The MSIX was created but is unsigned.'
    Write-Host '  You may need to run as admin or check certificate is in CurrentUser\My' -ForegroundColor DarkGray
}
else {
    Write-Host '  Package signed successfully' -ForegroundColor DarkGray
}

$sizeMB = [math]::Round((Get-Item $msixOutput).Length / 1MB, 2)
Write-Host ''
Write-Host ('  MSIX: ' + $msixOutput) -ForegroundColor Green
Write-Host ('  Size: ' + $sizeMB + ' MB') -ForegroundColor DarkGray

# ------------------------------------------------------------------------------
# 7. Run WACK (optional)
# ------------------------------------------------------------------------------

if ($RunWack) {
    Write-Host '[7/7] Running Windows App Certification Kit...' -ForegroundColor Yellow

    $wackPath = Find-SdkTool 'appcert.exe'
    if (-not $wackPath) {
        # Try the known path
        $wackPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\App Certification Kit\appcert.exe'
    }

    if (-not (Test-Path $wackPath)) {
        Write-Warning '  WACK not found. Install the Windows SDK.'
        Write-Host '  Run WACK manually: appcert.exe test -apptype desktop ...' -ForegroundColor DarkGray
    }
    else {
        $reportPath = Join-Path $ProjectRoot 'wack-report.xml'
        Write-Host ('  Validating: ' + $msixOutput) -ForegroundColor DarkGray

        & $wackPath test -apptype desktop -setuppath $msixOutput -reportoutputpath $reportPath

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
}
else {
    Write-Host '[7/7] Skipping WACK. Use -RunWack to enable.' -ForegroundColor DarkGray
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
Write-Host ('  Package:      ' + $msixOutput) -ForegroundColor Green
Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Yellow
Write-Host '    Sideload:     Add-AppxPackage -Path .\WardLock_1.0.0.0.msix' -ForegroundColor DarkGray
Write-Host '    Store submit: Upload MSIX to Partner Center' -ForegroundColor DarkGray
Write-Host '    Run WACK:     .\build-msix.ps1 -RunWack -SkipCert -SkipAssets' -ForegroundColor DarkGray
Write-Host ''

# Find your cert and trust it
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=WardLock-Dev' } | Select-Object -First 1
Export-Certificate -Cert $cert -FilePath "$env:TEMP\wardlock-dev.cer" | Out-Null
Import-Certificate -FilePath "$env:TEMP\wardlock-dev.cer" -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
Remove-Item "$env:TEMP\wardlock-dev.cer"
