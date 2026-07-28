<#
.SYNOPSIS
    Publishes CloudDrive and compiles the Inno Setup installer.

.DESCRIPTION
    The version is read from Directory.Build.props rather than passed in, so a release bumps one
    number in one place and the installer, the assemblies and the update feed all agree. A mismatch
    there is not cosmetic: the updater compares the running assembly version against the release tag
    to decide whether an update is needed, so a disagreement means either a permanent "update
    available" or an update that never applies.

    Requires Inno Setup 6+ (https://jrsoftware.org/isdl.php).
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publishDir = Join-Path $root 'publish'
$installerDir = Join-Path $root 'installer'

# --- Version ------------------------------------------------------------------------------------
[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
Write-Host "Building CloudDrive $version"

# --- Prerequisites ------------------------------------------------------------------------------
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    $iscc = (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue).Source
}
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php.'
}

$winfsp = Join-Path $root 'third_party\winfsp\winfsp.msi'
if (-not (Test-Path $winfsp)) {
    # The installer bundles WinFsp so a fresh machine can mount immediately. Without it the setup
    # would compile but produce an installer that leaves every drive mapping broken.
    throw 'third_party\winfsp\winfsp.msi is missing. Run scripts\fetch-tools.ps1 first.'
}

# --- Publish ------------------------------------------------------------------------------------
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
} elseif (-not (Test-Path $publishDir)) {
    throw "-SkipPublish was given but $publishDir does not exist."
}

# --- Compile ------------------------------------------------------------------------------------
Write-Host 'Compiling the installer...'
& $iscc `
    "/DAppVersion=$version" `
    "/DSourceDir=$publishDir" `
    (Join-Path $installerDir 'CloudDrive.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }

$setup = Join-Path $installerDir 'output\CloudDrive-Setup.exe'
if (-not (Test-Path $setup)) { throw 'ISCC reported success but produced no installer.' }

# Keep a versioned copy alongside the stable filename. The updater downloads the asset by pattern,
# and a release with two identically named installers from different builds is impossible to audit.
$versioned = Join-Path $installerDir "output\CloudDrive-Setup-$version.exe"
Copy-Item $setup $versioned -Force

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ''
Write-Host "Built CloudDrive-Setup.exe ($size MB)"
Write-Host "  $setup"
Write-Host "  $versioned"
