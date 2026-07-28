<#
.SYNOPSIS
    Downloads rclone and WinFsp into third_party/ for a development build.

.DESCRIPTION
    At runtime CloudDrive manages these itself, under %ProgramData%\CloudDrive\tools, and verifies
    every download against the vendor's digest and Authenticode signature (see ToolManager). This
    script is the build-time equivalent: it seeds third_party/ so a freshly cloned tree can build and
    run without the service having fetched anything yet, and so the installer has something to bundle.

    Binaries are deliberately not committed. They are large, they are not ours to redistribute, and a
    committed copy would bypass the verification the runtime path performs.
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $PSScriptRoot '..'),
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$thirdParty = Join-Path (Resolve-Path $Root) 'third_party'

function Get-LatestRelease {
    param([string] $Repo)
    $headers = @{ 'User-Agent' = 'CloudDrive-fetch-tools'; 'Accept' = 'application/vnd.github+json' }
    Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}

function Assert-Signature {
    param([string] $Path)
    # The same rule the runtime tool manager applies: an unsigned binary is refused rather than
    # installed with a warning, because this one ends up on the system PATH.
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid') {
        throw "$Path is not validly signed (status: $($signature.Status)). Refusing to use it."
    }
    Write-Host "  signature OK - $($signature.SignerCertificate.Subject.Split(',')[0])"
}

# ------------------------------------------------------------------ rclone ---------------------
$rcloneDir = Join-Path $thirdParty 'rclone'
$rcloneExe = Join-Path $rcloneDir 'rclone.exe'

if ((Test-Path $rcloneExe) -and -not $Force) {
    Write-Host "rclone already present: $rcloneExe"
} else {
    Write-Host 'Looking up the latest rclone release...'
    $release = Get-LatestRelease -Repo 'rclone/rclone'

    # Exclude the "osarch" bundle, which packs every platform into one archive: hundreds of
    # megabytes to extract a single exe.
    $asset = $release.assets |
        Where-Object { $_.name -like '*windows*amd64*.zip' -and $_.name -notlike '*osarch*' } |
        Select-Object -First 1
    if (-not $asset) { throw 'No Windows x64 rclone asset in the latest release.' }

    Write-Host "  $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
    New-Item -ItemType Directory -Force $rcloneDir | Out-Null

    $zip = Join-Path $env:TEMP $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing

    $unpack = Join-Path $env:TEMP ("rclone-" + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $zip -DestinationPath $unpack -Force

    # The payload sits inside a versioned directory whose name changes every release, so it is found
    # rather than assumed.
    $found = Get-ChildItem -Path $unpack -Filter 'rclone.exe' -Recurse | Select-Object -First 1
    if (-not $found) { throw 'rclone.exe was not in the archive; the vendor may have changed its layout.' }

    Assert-Signature -Path $found.FullName
    Copy-Item $found.FullName $rcloneExe -Force
    Set-Content -Path (Join-Path $rcloneDir 'VERSION.txt') -Value $release.tag_name

    Remove-Item $zip, $unpack -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  installed $($release.tag_name) -> $rcloneExe"
}

# ------------------------------------------------------------------ WinFsp ---------------------
$winfspDir = Join-Path $thirdParty 'winfsp'
$winfspMsi = Join-Path $winfspDir 'winfsp.msi'

if ((Test-Path $winfspMsi) -and -not $Force) {
    Write-Host "WinFsp already present: $winfspMsi"
} else {
    Write-Host 'Looking up the latest WinFsp release...'
    $release = Get-LatestRelease -Repo 'winfsp/winfsp'
    $asset = $release.assets | Where-Object { $_.name -like 'winfsp*.msi' } | Select-Object -First 1
    if (-not $asset) { throw 'No WinFsp MSI in the latest release.' }

    Write-Host "  $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
    New-Item -ItemType Directory -Force $winfspDir | Out-Null
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $winfspMsi -UseBasicParsing

    Assert-Signature -Path $winfspMsi
    Set-Content -Path (Join-Path $winfspDir 'SOURCE.txt') -Value @"
$($asset.browser_download_url)
$($release.tag_name)
"@
    Write-Host "  installed $($release.tag_name) -> $winfspMsi"
}

Write-Host ''
Write-Host 'Done. Build with: dotnet build CloudDrive.slnx'
