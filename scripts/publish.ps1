<#
.SYNOPSIS
    Publishes CloudDrive as a self-contained win-x64 build ready for the installer.

.DESCRIPTION
    Self-contained on purpose. The service has to start at boot on a server that may never have had
    .NET installed, and "install the runtime first" is not an acceptable prerequisite for something
    whose job is to have drives ready before anyone signs in.

    All four executables publish into one directory, because ServiceControl.ResolveServiceExe and the
    drive-icon lookup both expect the service and the tray app to sit side by side.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $Output = (Join-Path $PSScriptRoot '..\publish')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [System.IO.Path]::GetFullPath($Output)

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
New-Item -ItemType Directory -Force $Output | Out-Null

$projects = @(
    'src\CloudDrive.App\CloudDrive.App.csproj',
    'src\CloudDrive.Service\CloudDrive.Service.csproj',
    'src\CloudDrive.Cli\CloudDrive.Cli.csproj'
)

foreach ($project in $projects) {
    $name = Split-Path $project -Leaf
    Write-Host "Publishing $name..."

    # Not trimmed: WPF does not support trimming, and the CloudFiles layer resolves types through
    # CsWin32-generated P/Invoke that a trimmer cannot see. A larger install is worth more than a
    # runtime MissingMethodException on a customer's server.
    dotnet publish (Join-Path $root $project) `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=embedded `
        --output $Output `
        --nologo `
        --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Publishing $name failed." }
}

# The installer bundles these; the service also fetches them itself at runtime, but a fresh install
# should be able to mount before its first update check.
$thirdParty = Join-Path $root 'third_party'
$rclone = Join-Path $thirdParty 'rclone\rclone.exe'
if (Test-Path $rclone) {
    Copy-Item $rclone $Output -Force
    Write-Host 'Bundled rclone.exe'
} else {
    Write-Warning 'rclone.exe was not found. Run scripts\fetch-tools.ps1 first.'
}

$icon = Join-Path $root 'src\CloudDrive.App\Assets\clouddrive.ico'
if (Test-Path $icon) { Copy-Item $icon $Output -Force }

$size = [math]::Round(((Get-ChildItem $Output -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ''
Write-Host "Published to $Output ($size MB)"
Get-ChildItem $Output -Filter '*.exe' | ForEach-Object { Write-Host "  $($_.Name)" }
