#Requires -Version 7.0
<#
.SYNOPSIS
    Composes assets/social-preview.png, the 1280x640 GitHub social card.

.DESCRIPTION
    GitHub's social preview is fixed at 1280x640 with a 2:1 ratio. Uploading the
    raw banner instead would be upscaled and letterboxed by GitHub's own layout,
    so this composes the two supplied assets onto that exact canvas once, here,
    where the result can be reviewed.

    The composition is deliberately plain: the wordmark above the key art on a
    black field, which is how both source assets are already drawn. Nothing is
    invented, recoloured or re-lettered — this only positions and scales the two
    supplied images. The scale factors are above 1 because both sources are small;
    that is safe precisely because the card is displayed at a few hundred pixels
    wide in a link unfurl, so the resampled detail is never seen at 1:1.

    Set the result in the repository's Settings > General > Social preview. There
    is no API for that field, so it stays a manual step.

.PARAMETER Scale
    Multiplier applied to the natural size of both assets. 1.0 keeps them at
    their supplied pixel dimensions.

.EXAMPLE
    pwsh -File tools/New-SocialPreview.ps1
#>
[CmdletBinding()]
param(
    [ValidateRange(0.25, 4.0)]
    [double]$Scale = 1.6
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$logoPath = Join-Path $root 'assets/logo.png'
$bannerPath = Join-Path $root 'assets/banner.jpg'
$destination = Join-Path $root 'assets/social-preview.png'

foreach ($path in @($logoPath, $bannerPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "missing asset: $path" }
}

Add-Type -AssemblyName System.Drawing

$canvasWidth = 1280
$canvasHeight = 640
# Both supplied assets are drawn on black, so black is the only background that
# does not introduce a visible seam where the sources end.
$background = [System.Drawing.Color]::FromArgb(255, 0, 0, 0)
$gap = [int](32 * $Scale)
# Margins keep the scaled block off every edge; $bias is the optical-centring nudge
# described at the DrawImage call below.
$margin = 28
$bias = 20

function Resolve-TargetSize {
    param([System.Drawing.Image]$Image)

    return [pscustomobject]@{
        Width  = [int][Math]::Round($Image.Width * $Scale)
        Height = [int][Math]::Round($Image.Height * $Scale)
    }
}

$logo = [System.Drawing.Image]::FromFile($logoPath)
$banner = [System.Drawing.Image]::FromFile($bannerPath)

try {
    $logoTarget = Resolve-TargetSize -Image $logo
    $bannerTarget = Resolve-TargetSize -Image $banner

    foreach ($target in @($logoTarget, $bannerTarget)) {
        if ($target.Width -gt $canvasWidth) {
            throw ("scaled asset is {0}px wide, wider than the {1}px canvas; lower -Scale" -f $target.Width, $canvasWidth)
        }
    }

    $blockHeight = $logoTarget.Height + $gap + $bannerTarget.Height
    if (($blockHeight + (2 * $margin)) -gt $canvasHeight) {
        throw ("scaled assets need {0}px of height plus {1}px margins, more than the {2}px canvas; lower -Scale" -f $blockHeight, $margin, $canvasHeight)
    }

    $canvas = [System.Drawing.Bitmap]::new(
        $canvasWidth, $canvasHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear($background)

        # A slight upward bias reads as centred where a mathematically centred
        # block reads as sitting low. It is clamped to the margin, because an
        # unclamped bias pushes the wordmark off the top edge at larger scales and
        # silently crops the title.
        $top = [int](($canvasHeight - $blockHeight) / 2) - $bias
        $top = [Math]::Max($margin, [Math]::Min($top, $canvasHeight - $blockHeight - $margin))

        $logoX = [int](($canvasWidth - $logoTarget.Width) / 2)
        $graphics.DrawImage($logo, [System.Drawing.Rectangle]::new($logoX, $top, $logoTarget.Width, $logoTarget.Height))

        $bannerX = [int](($canvasWidth - $bannerTarget.Width) / 2)
        $bannerY = $top + $logoTarget.Height + $gap
        $graphics.DrawImage($banner, [System.Drawing.Rectangle]::new($bannerX, $bannerY, $bannerTarget.Width, $bannerTarget.Height))
    }
    finally {
        $graphics.Dispose()
    }

    try {
        $canvas.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $canvas.Dispose()
    }
}
finally {
    $logo.Dispose()
    $banner.Dispose()
}

$written = Get-Item -LiteralPath $destination

# Assert the canvas, not the intent: GitHub re-crops the card if the ratio is off.
$check = [System.Drawing.Image]::FromFile($destination)
try {
    if ($check.Width -ne $canvasWidth -or $check.Height -ne $canvasHeight) {
        throw "wrote $($check.Width)x$($check.Height), expected ${canvasWidth}x${canvasHeight}"
    }
    $dimensions = "$($check.Width)x$($check.Height)"
}
finally {
    $check.Dispose()
}

Write-Host ("wrote {0} ({1:N0} KB) at {2}; scale {3}" -f `
    $written.Name, ($written.Length / 1KB), $dimensions, $Scale)
Write-Host "set it in Settings > General > Social preview (GitHub has no API for this field)"
