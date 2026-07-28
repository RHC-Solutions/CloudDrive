<#
.SYNOPSIS
    Builds src/CloudDrive.App/Assets/clouddrive.ico from the RHC Solutions mark.

.DESCRIPTION
    Windows picks a different frame out of an .ico depending on where it draws it -- 16px in the
    tray and title bar, 32px in Explorer's list views, 256px on the desktop and in the installer.
    Handing it a single large frame makes Windows downscale on the fly, which turns a crisp
    four-square mark into mush at 16px. So every frame is rendered separately at its target size.

    Frames are stored as PNG rather than as a BMP + AND-mask. Every Windows version CloudDrive
    supports reads PNG-compressed icon entries, and it keeps the alpha channel intact without the
    1-bit mask dance the old format needs.
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot '..\src\CloudDrive.App\Assets\rhc-logo.png'),
    [string] $Output = (Join-Path $PSScriptRoot '..\src\CloudDrive.App\Assets\clouddrive.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Source = [System.IO.Path]::GetFullPath($Source)
$Output = [System.IO.Path]::GetFullPath($Output)
if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$src = [System.Drawing.Image]::FromFile($Source)
try {
    $frames = foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)
            $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
        } finally { $g.Dispose() }

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
        $ms.Dispose()
    }
} finally { $src.Dispose() }

# ICONDIR (6 bytes) + one ICONDIRENTRY (16 bytes) per frame, then the frame payloads.
$out = [System.IO.File]::Create($Output)
try {
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0)                 # reserved
    $w.Write([uint16]1)                 # type 1 = icon
    $w.Write([uint16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($f in $frames) {
        # 256 is encoded as 0 in the single-byte width/height fields.
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $w.Write([byte]$dim)            # width
        $w.Write([byte]$dim)            # height
        $w.Write([byte]0)               # palette entries (0 = truecolour)
        $w.Write([byte]0)               # reserved
        $w.Write([uint16]1)             # colour planes
        $w.Write([uint16]32)            # bits per pixel
        $w.Write([uint32]$f.Bytes.Length)
        $w.Write([uint32]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) { $w.Write($f.Bytes) }
    $w.Flush()
} finally { $out.Dispose() }

Write-Host "Wrote $Output ($($frames.Count) frames: $($sizes -join ', ')px, $((Get-Item $Output).Length) bytes)"
