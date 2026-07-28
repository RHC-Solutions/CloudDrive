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
# Includes the per-user location, which is where winget puts Inno Setup by default — a machine-wide
# install under Program Files is no longer the common case, and looking only there meant a correctly
# installed compiler was reported as missing.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    $iscc = (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue).Source
}
if (-not $iscc) {
    throw @'
Inno Setup 6 was not found. Install it with:
  winget install JRSoftware.InnoSetup
or from https://jrsoftware.org/isdl.php
'@
}
Write-Host "Using $iscc"

function Invoke-WindowSelfTest {
    <#
      Opens every window in the published build and refuses to package one that cannot.

      This gate exists because two releases shipped a tray app that died on launch. WPF resolves
      resource keys and binding cultures when a window loads, not when it compiles, so a clean build
      and a green unit-test run say nothing about whether the UI can open. Both faults — an
      InvariantGlobalization setting that broke every binding, and a XAML-raised event touching
      controls that did not exist yet — were invisible until something actually created a Window.
    #>
    param([string] $PublishDir)

    $exe = Join-Path $PublishDir 'CloudDrive.exe'
    if (-not (Test-Path $exe)) { throw "CloudDrive.exe is missing from $PublishDir." }

    $report = Join-Path $env:TEMP 'clouddrive-selftest.txt'
    Write-Host 'Running the window self-test...'

    # The report goes to a file because a WinExe writes nothing usable to a redirected console.
    $proc = Start-Process -FilePath $exe -ArgumentList '--selftest', "`"$report`"" `
        -Wait -PassThru -WindowStyle Hidden

    if (Test-Path $report) { Get-Content $report | ForEach-Object { Write-Host "  $_" } }

    if ($proc.ExitCode -ne 0) {
        throw "$($proc.ExitCode) window(s) failed to load. Refusing to build an installer for a UI that cannot start."
    }
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

# --- Verify the UI actually opens ---------------------------------------------------------------
Invoke-WindowSelfTest -PublishDir $publishDir

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
