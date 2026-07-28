<#
.SYNOPSIS
    Downloads rclone and WinFsp into third_party/ for a development build.

.DESCRIPTION
    At runtime CloudDrive manages these itself, under %ProgramData%\CloudDrive\tools, and verifies
    every download against the digest GitHub publishes for the asset, and against an Authenticode
    signature where the vendor provides one (see ToolManager). This
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

function Assert-Digest {
    <#
      Verifies a download against the SHA-256 digest GitHub publishes for the asset.

      This is the primary check, not the signature, because that is what these vendors actually
      publish: rclone ships unsigned Windows binaries with a SHA256SUMS file. The digest comes from
      the API response rather than from the downloaded bytes, so it is an independent attestation.
    #>
    param([string] $Path, [string] $ExpectedDigest, [string] $Name)

    if (-not $ExpectedDigest) {
        throw "GitHub published no digest for $Name. Refusing to use an unverified download."
    }
    $expected = $ExpectedDigest -replace '^sha256:', ''
    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash

    if ($actual -ine $expected) {
        Remove-Item $Path -Force -ErrorAction SilentlyContinue
        throw "$Name failed verification. Expected $expected, got $actual. The download was discarded."
    }
    Write-Host "  SHA-256 verified: $($actual.ToLower())"
}

function Assert-Signature {
    <#
      Validates an Authenticode signature when one is present.

      An invalid signature is fatal. A missing one is fatal only when -Required is given: WinFsp is
      signed and installs a kernel driver, while rclone is not signed at all and requiring it would
      reject the one tool CloudDrive cannot work without.
    #>
    param([string] $Path, [switch] $Required)

    $signature = Get-AuthenticodeSignature -FilePath $Path

    if ($signature.Status -eq 'Valid') {
        Write-Host "  signature OK - $($signature.SignerCertificate.Subject.Split(',')[0])"
        return
    }

    if ($signature.Status -eq 'NotSigned') {
        if ($Required) {
            throw "$Path is not signed, and it must be - it installs a kernel-mode driver."
        }
        Write-Host '  not signed (this vendor does not sign); relying on the digest check'
        return
    }

    throw "$Path has an invalid signature (status: $($signature.Status)). Refusing to use it."
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
    Assert-Digest -Path $zip -ExpectedDigest $asset.digest -Name $asset.name

    $unpack = Join-Path $env:TEMP ("rclone-" + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $zip -DestinationPath $unpack -Force

    # The payload sits inside a versioned directory whose name changes every release, so it is found
    # rather than assumed.
    $found = Get-ChildItem -Path $unpack -Filter 'rclone.exe' -Recurse | Select-Object -First 1
    if (-not $found) { throw 'rclone.exe was not in the archive; the vendor may have changed its layout.' }

    # The archive is what was verified; the exe inside inherits that. rclone does not sign, so this
    # only reports rather than gates.
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
    Assert-Digest -Path $winfspMsi -ExpectedDigest $asset.digest -Name $asset.name
    # A kernel-mode driver: this one must be signed.
    Assert-Signature -Path $winfspMsi -Required
    Set-Content -Path (Join-Path $winfspDir 'SOURCE.txt') -Value @"
$($asset.browser_download_url)
$($release.tag_name)
"@
    Write-Host "  installed $($release.tag_name) -> $winfspMsi"
}

Write-Host ''
Write-Host 'Done. Build with: dotnet build CloudDrive.slnx'
