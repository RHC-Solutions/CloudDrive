<#
.SYNOPSIS
    Rewrites the README download link, version and SHA-256 to match the built installer.

.DESCRIPTION
    The README advertises a specific tag rather than releases/latest/download, because GitHub's "latest"
    excludes prereleases and that shortcut 404s while every release is flagged as one. A hard-coded tag
    goes stale silently, and a stale checksum is worse than none: it tells a careful user their download
    is corrupt when it is fine.

    So this reads the version from Directory.Build.props, hashes the installer that was actually built,
    and updates all three together. Run it after scripts\build-installer.ps1 and before publishing.

    Once a stable (non-prerelease) release exists, the link can be replaced with
    releases/latest/download/CloudDrive-Setup.exe and this script becomes unnecessary.
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $PSScriptRoot '..'),
    [switch] $WhatIfOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = Resolve-Path $Root
$readme = Join-Path $Root 'README.md'
$setup = Join-Path $Root 'installer\output\CloudDrive-Setup.exe'

[xml] $props = Get-Content (Join-Path $Root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

if (-not (Test-Path $setup)) {
    throw "$setup does not exist. Run scripts\build-installer.ps1 first."
}

$hash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB)

$text = Get-Content $readme -Raw
$original = $text

# The heading carries both the version and the tagged URL.
$text = [regex]::Replace(
    $text,
    '### \[⬇ CloudDrive-Setup\.exe — [^\]]+\]\(https://github\.com/RHC-Solutions/CloudDrive/releases/download/v[^/]+/CloudDrive-Setup\.exe\)',
    "### [⬇ CloudDrive-Setup.exe — $version](https://github.com/RHC-Solutions/CloudDrive/releases/download/v$version/CloudDrive-Setup.exe)")

$text = [regex]::Replace($text, '^\d+ MB · Windows', "$sizeMb MB · Windows", 'Multiline')

# The only bare 64-hex line in the README is the installer digest.
$text = [regex]::Replace($text, '(?m)^[0-9a-f]{64}$', $hash)

# Any prose mentioning the previous version in the download block.
$text = $text -replace '\*\*\d+\.\d+\.\d+ is a prerelease\.\*\*', "**$version is a prerelease.**"

if ($text -eq $original) {
    Write-Host "README already matches $version ($hash)."
    return
}

if ($WhatIfOnly) {
    Write-Host "Would update the README to $version, $sizeMb MB, $hash"
    return
}

Set-Content -Path $readme -Value $text -NoNewline
Write-Host "README updated: $version, $sizeMb MB"
Write-Host "  SHA-256 $hash"

# Verify the advertised link actually resolves, because a tag that has not been published yet produces
# a README that looks correct and 404s for every reader.
$url = "https://github.com/RHC-Solutions/CloudDrive/releases/download/v$version/CloudDrive-Setup.exe"
try {
    $response = Invoke-WebRequest -Uri $url -Method Head -MaximumRedirection 5 -ErrorAction Stop
    Write-Host "  link OK ($($response.StatusCode))"
} catch {
    Write-Warning "The download link does not resolve yet: $url"
    Write-Warning 'Publish the release, then re-run this script to confirm.'
}
