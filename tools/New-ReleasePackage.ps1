#Requires -Version 7.0
<#
.SYNOPSIS
    Packages a published build into a release archive.

.DESCRIPTION
    Takes dist/ (from tools/Publish-Exe.ps1) and produces a zip suitable for a
    GitHub release asset.

    The disc image is deliberately EXCLUDED. dist/ normally has Bio2Nov96.bin
    copied beside the exe for convenience, but that file is Capcom's and is never
    redistributed; a release archive that carried it would be redistributing the
    game. The cue sheet IS included, because it is a plain-text track layout that
    is already tracked in this repository and contains no disc data - the user
    still supplies the disc it describes.

    A short README is written into the archive explaining the one thing a
    downloader has to do, since the exe will otherwise report that it cannot find
    a disc.

.PARAMETER DistDirectory
    Published build to package. Defaults to <repo root>/dist.

.PARAMETER OutputDirectory
    Where the archive is written. Defaults to <repo root>/release.

.PARAMETER Version
    Version string used in the file name and the bundled README.

.PARAMETER SkipPublish
    Do not run tools/Publish-Exe.ps1 first. By default a release always republishes,
    so the archive cannot accidentally contain a stale build.

.EXAMPLE
    pwsh -File tools/New-ReleasePackage.ps1

.EXAMPLE
    pwsh -File tools/New-ReleasePackage.ps1 -Version 0.3.1
#>
[CmdletBinding()]
param(
    [string]$DistDirectory,
    [string]$OutputDirectory,
    [string]$Version = '0.3.0',
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $DistDirectory) { $DistDirectory = Join-Path $root 'dist' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'release' }

if (-not $SkipPublish) {
    Write-Host 'publishing a fresh build first'
    & (Join-Path $PSScriptRoot 'Publish-Exe.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Publish-Exe.ps1 failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path -LiteralPath (Join-Path $DistDirectory 'RE15pc.exe'))) {
    throw "no RE15pc.exe in $DistDirectory; run tools/Publish-Exe.ps1 first"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stage = Join-Path $OutputDirectory 'stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Never ship the disc. Named rather than pattern-matched so the exclusion is
# obvious to a reader of this script and to anyone auditing the archive.
$excluded = @('Bio2Nov96.bin')

# Debug symbols are dropped too: RE15pc.pdb alone is ~14 MB of the archive, it is
# useless without the exact source revision, and it carries symbol names for the
# recompiled guest. A release does not need it.
$excludedExtensions = @('.pdb')

$copied = 0
$bytes = 0L
foreach ($item in Get-ChildItem -LiteralPath $DistDirectory -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($DistDirectory, $item.FullName)
    if ($excluded -contains $item.Name) {
        Write-Host "  excluding $relative (disc image, not redistributed)"
        continue
    }
    if ($excludedExtensions -contains $item.Extension) {
        Write-Host "  excluding $relative (debug symbols)"
        continue
    }
    $target = Join-Path $stage $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $item.FullName -Destination $target -Force
    $copied++
    $bytes += $item.Length
}

$readme = @"
RE15pc $Version - Biohazard 2 (November 6, 1996) prototype, a.k.a. Resident Evil 1.5
====================================================================================

This is a native Windows x64 build. It carries its own .NET runtime, so nothing
else needs to be installed.

BEFORE YOU RUN IT
-----------------
You must supply your own copy of the prototype disc. It is not included here and
never will be: the disc is Capcom's, this archive does not contain it, and no
recompiled output from it is distributed either.

Put both of these next to RE15pc.exe:

    Bio2Nov96.bin      the disc image
    Bio2Nov96.cue      the cue sheet - already included in this archive

Then run RE15pc.exe. You can also point it at a disc elsewhere:

    RE15pc.exe --cue "D:\path\to\Bio2Nov96.cue"

The disc is identified by SHA-256, so a different or modified image is refused
rather than half-working.

DISPLAY
-------
The window is sized from your monitor, so no configuration is needed. If you want
to be explicit:

    RE15pc.exe --window-mode borderless      fill the screen, no resolution change
    RE15pc.exe --resolution 1920x1080        a specific window size
    RE15pc.exe --window-mode fullscreen      exclusive fullscreen
    RE15pc.exe --help                        everything else

Borderless is the recommended full-screen mode: it never asks the driver to change
display mode, so it cannot pick something your monitor does not support.

CONTROLLERS
-----------
Xbox pads work through SDL's XInput backend. Bluetooth and other pads fall back to
HIDAPI. Keyboard bindings and per-pad configuration are in Settings.

NOT AFFILIATED WITH CAPCOM
--------------------------
This is an unofficial, non-commercial preservation project. Resident Evil and
Biohazard are trademarks of Capcom Co., Ltd. No Capcom code, art, audio, disc
image or BIOS is redistributed.
"@
Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Value $readme -Encoding UTF8
$copied++

$archiveName = "RE15pc-$Version-win-x64.zip"
$archivePath = Join-Path $OutputDirectory $archiveName
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }

Write-Host "compressing $copied files ($([Math]::Round($bytes / 1MB, 1)) MB)"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archivePath -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force

$archive = Get-Item -LiteralPath $archivePath
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash

Write-Host ''
Write-Host ("packaged {0}" -f $archive.Name)
Write-Host ("  size    {0:N1} MB" -f ($archive.Length / 1MB))
Write-Host ("  files   {0}" -f $copied)
Write-Host ("  sha256  {0}" -f $hash)
Write-Host ("  path    {0}" -f $archive.FullName)
