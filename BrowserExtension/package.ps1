<#
.SYNOPSIS
    Builds per-browser copies of the WardLock extension.

.DESCRIPTION
    Chrome/Edge and Firefox need different manifests (pinned "key" +
    background.service_worker vs. browser_specific_settings.gecko +
    background.scripts), so the shared scripts are copied into
    dist\chromium and dist\firefox with the right manifest.json each, and
    zipped. The Firefox manifest's version is taken from manifest.json so the
    two never drift.

    Chrome/Edge can still load this folder directly (Load unpacked);
    Firefox loads dist\firefox (about:debugging → Load Temporary Add-on).

.PARAMETER OutDir
    Output directory. Default: BrowserExtension\dist

.PARAMETER ZipPrefix
    Prefix for the zip file names. Default: WardLock
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'dist'),
    [string]$ZipPrefix = 'WardLock'
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$shared = Get-ChildItem $src -File | Where-Object {
    $_.Extension -in '.js', '.html', '.css', '.png' -and $_.Name -notlike 'manifest*'
}

# [IO.File] reads UTF-8 regardless of the PowerShell edition's default codepage.
$chromeText = [IO.File]::ReadAllText((Join-Path $src 'manifest.json'))
$firefoxText = [IO.File]::ReadAllText((Join-Path $src 'manifest.firefox.json'))
$version = ($chromeText | ConvertFrom-Json).version
$versionPattern = [regex]'"version"\s*:\s*"[^"]*"'
$firefoxText = $versionPattern.Replace($firefoxText, ('"version": "' + $version + '"'), 1)

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$targets = @{
    chromium = $chromeText
    firefox  = $firefoxText
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

foreach ($browser in $targets.Keys) {
    $dir = Join-Path $OutDir $browser
    New-Item $dir -ItemType Directory -Force | Out-Null
    $shared | Copy-Item -Destination $dir
    [IO.File]::WriteAllText((Join-Path $dir 'manifest.json'), $targets[$browser], $utf8NoBom)

    $zip = Join-Path $OutDir "$ZipPrefix-extension-$browser.zip"
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip -Force
    Write-Host "  $browser -> $zip"
}
