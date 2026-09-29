#Requires -Version 7.0
<#
.SYNOPSIS
    Builds assets/icon.ico, the Windows icon embedded in RE15pc.exe.

.DESCRIPTION
    The source art is assets/icon.png (512x512, RGBA). Windows picks a different
    frame out of the .ico depending on context: 16 px in the title bar and the
    taskbar's small mode, 32 px in Alt-Tab, 48 px in Explorer's medium view,
    256 px in the extra-large view and on the desktop. An .ico carrying one
    downscaled bitmap therefore looks soft in most of those places, so this
    script emits the full ladder.

    Layout follows the convention every Windows icon parser expects: frames up
    to 128 px are stored as uncompressed 32-bit DIBs with an AND mask, and the
    256 px frame is stored as PNG. A DIB frame cannot encode a 256 px
    dimension, and a PNG frame is what shell versions since Vista expect there.

    The AND mask is written as all-zero and the real transparency comes from the
    DIB's alpha channel. That is the Vista-era behaviour: on a legacy renderer
    the zero mask means "draw the pixel" and the alpha channel is ignored, which
    degrades to a square icon rather than an invisible one.

.PARAMETER Source
    PNG to read. Defaults to <repo root>/assets/icon.png.

.PARAMETER Destination
    .ico to write. Defaults to <repo root>/assets/icon.ico.

.EXAMPLE
    pwsh -File tools/New-AppIcon.ps1
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'assets/icon.png' }
if (-not $Destination) { $Destination = Join-Path $root 'assets/icon.ico' }

if (-not (Test-Path -LiteralPath $Source)) {
    throw "source image not found: $Source"
}

Add-Type -AssemblyName System.Drawing

# DIB frames carry real alpha; PNG is only used where a DIB cannot go.
$dibSizes = @(16, 24, 32, 48, 64, 128)
$pngSizes = @(256)
$sizes = @($dibSizes + $pngSizes | Sort-Object)

function New-ResizedBitmap {
    param([System.Drawing.Image]$Image, [int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        # HighQualityBicubic alone leaves a faint halo around high-contrast
        # edges when downscaling by 32x, so the surround is premultiplied
        # against transparent black before the resample.
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage(
            $Image,
            [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
            0, 0, $Image.Width, $Image.Height,
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function ConvertTo-DibFrame {
    <#
        ICONDIRENTRY images stored as DIBs are a BITMAPINFOHEADER whose height is
        doubled to cover the XOR colour bitmap plus the AND mask, followed by
        bottom-up BGRA rows and then the 1bpp mask rows padded to 4 bytes.
    #>
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $stride = $size * 4
    $maskStride = [int]([Math]::Floor(($size + 31) / 32) * 4)

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint32]40)                 # biSize
        $writer.Write([int32]$size)               # biWidth
        $writer.Write([int32]($size * 2))         # biHeight = XOR + AND
        $writer.Write([uint16]1)                  # biPlanes
        $writer.Write([uint16]32)                 # biBitCount
        $writer.Write([uint32]0)                  # biCompression = BI_RGB
        $writer.Write([uint32]($stride * $size))  # biSizeImage
        $writer.Write([int32]0)                   # biXPelsPerMeter
        $writer.Write([int32]0)                   # biYPelsPerMeter
        $writer.Write([uint32]0)                  # biClrUsed
        $writer.Write([uint32]0)                  # biClrImportant

        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $Bitmap.GetPixel($x, $y)
                $writer.Write([byte]$c.B)  # BGRA order
                $writer.Write([byte]$c.G)
                $writer.Write([byte]$c.R)
                $writer.Write([byte]$c.A)
            }
        }

        # All-zero AND mask: alpha is authoritative on every renderer that
        # matters, and this stays well-defined on the ones that ignore it.
        $empty = [byte[]]::new($maskStride * $size)
        $writer.Write($empty)

        $writer.Flush()
        # The leading comma suppresses PowerShell's pipeline unrolling. Without
        # it the byte[] is decomposed into object[] and the later
        # BinaryWriter.Write call binds to a scalar overload instead of the
        # array one, which silently emits a corrupt one-byte frame.
        return ,$stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

function ConvertTo-PngFrame {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

$sourceImage = [System.Drawing.Image]::FromFile($Source)
if ($sourceImage.Width -ne $sourceImage.Height) {
    $sourceImage.Dispose()
    throw "source must be square, got $($sourceImage.Width)x$($sourceImage.Height)"
}

try {
    $frames = [System.Collections.Generic.List[object]]::new()
    foreach ($size in $sizes) {
        $bitmap = New-ResizedBitmap -Image $sourceImage -Size $size
        try {
            $bytes = if ($pngSizes -contains $size) {
                ConvertTo-PngFrame -Bitmap $bitmap
            }
            else {
                ConvertTo-DibFrame -Bitmap $bitmap
            }
            $frames.Add([pscustomobject]@{ Size = $size; Bytes = [byte[]]$bytes })
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $sourceImage.Dispose()
}

$outStream = [System.IO.MemoryStream]::new()
$out = [System.IO.BinaryWriter]::new($outStream)
try {
    $out.Write([uint16]0)                  # reserved
    $out.Write([uint16]1)                  # type = icon
    $out.Write([uint16]$frames.Count)

    # Directory entries precede every image, so offsets start past the header.
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        # A stored dimension byte is 0 when the real dimension is 256.
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $out.Write([byte]$dimension)       # width
        $out.Write([byte]$dimension)       # height
        $out.Write([byte]0)                # colour count (0 = truecolour)
        $out.Write([byte]0)                # reserved
        $out.Write([uint16]1)              # colour planes
        $out.Write([uint16]32)             # bits per pixel
        $out.Write([uint32]$frame.Bytes.Length)
        $out.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $out.Write([byte[]]$frame.Bytes) }

    $out.Flush()
    $destinationDirectory = Split-Path -Parent $Destination
    if ($destinationDirectory -and -not (Test-Path -LiteralPath $destinationDirectory)) {
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    }
    [System.IO.File]::WriteAllBytes($Destination, $outStream.ToArray())
}
finally {
    $out.Dispose()
    $outStream.Dispose()
}

$written = Get-Item -LiteralPath $Destination

# Read the file back rather than trusting the writer. A frame whose directory
# entry disagrees with its payload is the exact failure that produced a
# plausible-looking but unusable icon before, so it is asserted here.
$raw = [System.IO.File]::ReadAllBytes($Destination)
$reader = [System.IO.BinaryReader]::new([System.IO.MemoryStream]::new($raw, $false))
try {
    $reserved = $reader.ReadUInt16()
    $type = $reader.ReadUInt16()
    $count = $reader.ReadUInt16()
    if ($reserved -ne 0 -or $type -ne 1) { throw "not an ICO file: reserved=$reserved type=$type" }
    if ($count -ne $frames.Count) { throw "frame count $count does not match $($frames.Count)" }

    $payloadTotal = 0
    for ($i = 0; $i -lt $count; $i++) {
        $null = $reader.ReadBytes(4)          # width, height, colour count, reserved
        $null = $reader.ReadUInt16()          # planes
        $null = $reader.ReadUInt16()          # bits per pixel
        $declared = $reader.ReadUInt32()
        $position = $reader.ReadUInt32()
        if ($declared -le 1) { throw "frame $i declares $declared bytes" }
        if (([long]$position + $declared) -gt $raw.Length) { throw "frame $i runs past end of file" }
        $payloadTotal += $declared
    }
    if (($payloadTotal + 6 + (16 * $count)) -ne $raw.Length) {
        throw "declared payloads ($payloadTotal) do not account for the file ($($raw.Length) bytes)"
    }
}
finally {
    $reader.Dispose()
}

# A final round trip through the shell's own decoder, which rejects a ladder the
# hand-rolled header check would happily accept.
$icon = [System.Drawing.Icon]::new($Destination)
try {
    $frameSizes = ($icon.Width, $icon.Height) -join 'x'
}
finally {
    $icon.Dispose()
}

Write-Host ("wrote {0} ({1} bytes): {2} frames at {3} px; shell default frame {4}" -f `
    $written.Name, $written.Length, $frames.Count, ($sizes -join ', '), $frameSizes)
