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

# Assembly names must be distinct case-insensitively, because publishing several projects into one
# directory on Windows means a collision silently overwrites one executable with another. This
# already happened once: the CLI was called clouddrive.exe, which replaced the tray app's
# CloudDrive.exe, and the only symptom was a missing GUI. Checked up front rather than left to a user.
$assemblyNames = foreach ($project in $projects) {
    [xml] $proj = Get-Content (Join-Path $root $project)
    $name = $proj.Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
    if (-not $name) { throw "$project does not set <AssemblyName>." }
    $name
}
$collisions = $assemblyNames | Group-Object -Property { $_.ToLowerInvariant() } |
    Where-Object { $_.Count -gt 1 }
if ($collisions) {
    throw ("Assembly names collide case-insensitively and would overwrite each other: " +
           ($collisions.Group -join ', '))
}

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

# Confirm each expected executable actually survived the publish. A missing one here means something
# overwrote it, which is not something to leave for a user to notice.
$expected = @('CloudDrive.exe', 'CloudDrive.Service.exe', 'cdrive.exe')
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $Output $_)) }
if ($missing) { throw "Publish is incomplete; these are missing: $($missing -join ', ')" }

$size = [math]::Round(((Get-ChildItem $Output -Recurse | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ''
Write-Host "Published to $Output ($size MB)"
foreach ($exe in $expected) {
    $item = Get-Item (Join-Path $Output $exe)
    Write-Host ("  {0,-24} {1,7:N1} MB" -f $item.Name, ($item.Length / 1MB))
}
