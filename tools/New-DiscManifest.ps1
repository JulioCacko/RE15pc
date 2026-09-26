<#
.SYNOPSIS
    Regenerates disc-manifest.json from the prototype disc image.

.DESCRIPTION
    The manifest is the port's contract with the disc. Every later phase asserts
    against it, so that all debugging happens against one provably identical
    image and a wrong image fails immediately rather than producing garbage.

    It records the image geometry, the ISO9660 volume details, the boot
    executable's PS-X EXE header, the seven code overlays with their load base,
    and the complete file table.

    Only metadata is recorded. No game data is written.

.PARAMETER Cue
    Path to the cue sheet. Defaults to Bio2Nov96.cue in the repository root.

.PARAMETER Out
    Path of the manifest to write. Defaults to disc-manifest.json in the
    repository root.

.PARAMETER Check
    Verify the existing manifest matches the disc instead of rewriting it.
    Exits non-zero on any difference. Intended for CI and for confirming that a
    failure is not caused by the wrong disc.

.EXAMPLE
    pwsh -File tools/New-DiscManifest.ps1

.EXAMPLE
    pwsh -File tools/New-DiscManifest.ps1 -Check

.NOTES
    Written to run on both Windows PowerShell 5.1 and PowerShell 7+. Hex
    literals between 0x80000000 and 0xFFFFFFFF are parsed as negative Int32 by
    Windows PowerShell, so any such comparison uses an explicit [int64] cast of
    a decimal constant.
#>
[CmdletBinding()]
param(
    [string] $Cue,
    [string] $Out,
    [switch] $Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Cue) { $Cue = Join-Path $RepoRoot 'Bio2Nov96.cue' }
if (-not $Out) { $Out = Join-Path $RepoRoot 'disc-manifest.json' }

if (-not (Test-Path -LiteralPath $Cue)) {
    throw "cue sheet not found: $Cue"
}

# ---------------------------------------------------------------------------
# Parse the cue sheet for the image path and declared track mode.
# ---------------------------------------------------------------------------
$binName = $null
$trackMode = $null
foreach ($line in Get-Content -LiteralPath $Cue) {
    $t = $line.Trim()
    if ($t -match '^FILE\s+"(.+?)"\s') { $binName = $Matches[1] }
    elseif ($t -match '^TRACK\s+01\s+(\S+)') { $trackMode = $Matches[1] }
}

if (-not $binName) { throw "could not find a FILE entry in $Cue" }
if (-not $trackMode) { throw "could not find a TRACK 01 entry in $Cue" }

$BinPath = Join-Path (Split-Path -Parent $Cue) $binName
if (-not (Test-Path -LiteralPath $BinPath)) {
    throw "disc image not found: $BinPath"
}

$fi = Get-Item -LiteralPath $BinPath
$SectorSize = 2352

if (($fi.Length % $SectorSize) -ne 0) {
    throw "$binName is $($fi.Length) bytes, not a whole number of ${SectorSize}-byte sectors."
}
$SectorCount = [int]($fi.Length / $SectorSize)

Write-Host "[manifest] $binName : $($fi.Length) bytes, $SectorCount sectors"

# ---------------------------------------------------------------------------
# The port only understands MODE2/2352. A MODE0 or MODE1 declaration almost
# always means a hand-written or mishandled cue sheet; warn rather than fail,
# because RecompOne's reader tolerates it but other tools will not.
# ---------------------------------------------------------------------------
if ($trackMode -ne 'MODE2/2352') {
    Write-Warning "cue declares track mode '$trackMode'; expected MODE2/2352."
}

# ---------------------------------------------------------------------------
# Raw sector access. MODE2/Form1 carries 2048 user bytes at offset 24.
# ---------------------------------------------------------------------------
$script:Stream = [System.IO.File]::Open(
    $BinPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::Read)

function Read-UserSectors {
    param([int] $Lba, [int] $Count)

    if ($Count -le 0) { return ,@() }
    $dest = New-Object byte[] ($Count * 2048)
    $sector = New-Object byte[] 2048

    for ($i = 0; $i -lt $Count; $i++) {
        $script:Stream.Seek(([long]($Lba + $i) * 2352) + 24, [System.IO.SeekOrigin]::Begin) | Out-Null
        $read = 0
        while ($read -lt 2048) {
            $n = $script:Stream.Read($sector, $read, 2048 - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        [Array]::Copy($sector, 0, $dest, $i * 2048, 2048)
    }

    return ,$dest
}

function Read-Extent {
    param([int] $Lba, [int] $Size)

    $sectors = [int][Math]::Ceiling($Size / 2048.0)
    $buf = Read-UserSectors -Lba $Lba -Count $sectors
    if ($Size -eq $buf.Length) { return ,$buf }

    $trimmed = New-Object byte[] $Size
    [Array]::Copy($buf, 0, $trimmed, 0, $Size)
    return ,$trimmed
}

function Get-Ascii {
    param([byte[]] $Data, [int] $Offset, [int] $Length)
    return [System.Text.Encoding]::ASCII.GetString($Data, $Offset, $Length).Trim()
}

try {
    # -----------------------------------------------------------------------
    # Primary Volume Descriptor
    # -----------------------------------------------------------------------
    $pvd = Read-UserSectors -Lba 16 -Count 1
    $pvdMagic = [System.Text.Encoding]::ASCII.GetString($pvd, 1, 5)
    if ($pvdMagic -ne 'CD001') {
        throw "no ISO9660 primary volume descriptor at LBA 16 (found '$pvdMagic'). " +
              'The image is not a raw MODE2/2352 PlayStation disc, or is truncated.'
    }

    $rootLba = [BitConverter]::ToInt32($pvd, 156 + 2)
    $rootSize = [BitConverter]::ToUInt32($pvd, 156 + 10)

    # -----------------------------------------------------------------------
    # Directory walk
    # -----------------------------------------------------------------------
    $files = New-Object System.Collections.Generic.List[object]

    function Walk-Directory {
        param([int] $Lba, [int] $Size, [string] $Prefix)

        $data = Read-Extent -Lba $Lba -Size $Size
        $i = 0

        while ($i -lt $data.Length) {
            $len = $data[$i]
            if ($len -eq 0) {
                # Advance to the next logical sector boundary.
                $i = ([int][Math]::Floor($i / 2048) + 1) * 2048
                continue
            }

            $eLba = [BitConverter]::ToInt32($data, $i + 2)
            $eSize = [BitConverter]::ToUInt32($data, $i + 10)
            $isDir = ($data[$i + 25] -band 0x02) -ne 0
            $nameLen = $data[$i + 32]

            # Skip the '.' and '..' entries (name length 1, byte 0 or 1).
            if ($nameLen -eq 1 -and ($data[$i + 33] -eq 0 -or $data[$i + 33] -eq 1)) {
                $i += $len
                continue
            }

            $raw = [System.Text.Encoding]::ASCII.GetString($data, $i + 33, $nameLen)
            $semi = $raw.IndexOf(';')
            if ($semi -ge 0) { $raw = $raw.Substring(0, $semi) }

            $full = if ($Prefix) { "$Prefix/$raw" } else { $raw }

            if ($isDir) {
                $files.Add([pscustomobject]@{ path = $full; lba = $eLba; size = $eSize; kind = 'directory' })
                Walk-Directory -Lba $eLba -Size $eSize -Prefix $full
            }
            else {
                $files.Add([pscustomobject]@{ path = $full; lba = $eLba; size = $eSize; kind = 'file' })
            }

            $i += $len
        }
    }

    Walk-Directory -Lba $rootLba -Size ([int]$rootSize) -Prefix ''

    Write-Host "[manifest] $($files.Count) directory entries"

    # -----------------------------------------------------------------------
    # SYSTEM.CNF
    # -----------------------------------------------------------------------
    $cnf = $files | Where-Object { $_.path -eq 'SYSTEM.CNF' } | Select-Object -First 1
    if (-not $cnf) { throw 'SYSTEM.CNF not found in the root directory.' }

    $cnfBytes = Read-Extent -Lba $cnf.lba -Size ([int]$cnf.size)
    $cnfText = [System.Text.Encoding]::ASCII.GetString($cnfBytes)

    $bootExe = $null
    foreach ($line in ($cnfText -split "`n")) {
        $clean = $line
        if ($clean.Contains(';')) { $clean = $clean.Substring(0, $clean.IndexOf(';')) }
        $parts = $clean -split '=', 2
        if ($parts.Count -ne 2) { continue }
        if ($parts[0].Trim() -ne 'BOOT') { continue }
        $value = $parts[1].Trim()
        $colon = $value.IndexOf(':')
        if ($colon -ge 0) { $value = $value.Substring($colon + 1) }
        $bootExe = $value.TrimStart('\', '/').Replace('\', '/')
    }

    if (-not $bootExe) { throw 'SYSTEM.CNF has no usable BOOT line.' }

    # -----------------------------------------------------------------------
    # Boot executable header
    # -----------------------------------------------------------------------
    $exe = $files | Where-Object { $_.path -eq $bootExe } | Select-Object -First 1
    if (-not $exe) { throw "boot executable '$bootExe' listed in SYSTEM.CNF is not on the disc." }

    $exeHead = Read-UserSectors -Lba $exe.lba -Count 1
    $exeMagic = [System.Text.Encoding]::ASCII.GetString($exeHead, 0, 8)
    if ($exeMagic -ne 'PS-X EXE') {
        throw "'$bootExe' does not begin with a PS-X EXE header (found '$exeMagic')."
    }

    $entryPc = [BitConverter]::ToUInt32($exeHead, 0x10)
    $loadAddr = [BitConverter]::ToUInt32($exeHead, 0x18)
    $textSize = [BitConverter]::ToUInt32($exeHead, 0x1C)

    # -----------------------------------------------------------------------
    # Overlays. All seven are alternate RAM images of one region based at
    # 0x80100000; see docs/phases.md for how the base was established.
    # -----------------------------------------------------------------------
    $overlayBase = '0x80100000'
    $overlayPaths = @(
        'PSX/BIN/STAGE1.BIN', 'PSX/BIN/STAGE2.BIN', 'PSX/BIN/STAGE3.BIN',
        'PSX/BIN/STAGE4.BIN', 'PSX/BIN/STAGE5.BIN', 'PSX/BIN/STAGE6.BIN',
        'PSX/BIN/TITLE.BIN'
    )

    $overlays = New-Object System.Collections.Generic.List[object]
    foreach ($path in $overlayPaths) {
        $entry = $files | Where-Object { $_.path -eq $path } | Select-Object -First 1
        if (-not $entry) {
            Write-Warning "expected overlay '$path' is missing from the disc."
            continue
        }

        # Overlay names must match RecompOne's CdUtils.OverlayName: the
        # lowercase basename without extension. The runtime keys overlay
        # dispatch on exactly this string.
        $name = [System.IO.Path]::GetFileNameWithoutExtension($path).ToLowerInvariant()

        $overlays.Add([pscustomobject]@{
                name = $name
                path = $entry.path
                lba  = $entry.lba
                size = $entry.size
                base = $overlayBase
            })
    }

    # -----------------------------------------------------------------------
    # Manifest
    # -----------------------------------------------------------------------
    $sha = (Get-FileHash -LiteralPath $BinPath -Algorithm SHA256).Hash

    $manifest = [ordered]@{
        '$schema'    = 'RE15pc disc manifest v1'
        generatedBy  = 'tools/New-DiscManifest.ps1'
        disc         = [ordered]@{
            file       = $binName
            cue        = [System.IO.Path]::GetFileName($Cue)
            bytes      = $fi.Length
            sha256     = $sha
            sectorSize = $SectorSize
            sectors    = $SectorCount
            trackMode  = $trackMode
            dataOffset = 24
        }
        volume       = [ordered]@{
            systemId   = Get-Ascii $pvd 8 32
            volumeId   = Get-Ascii $pvd 40 32
            pvdLba     = 16
            rootDirLba = $rootLba
            rootDirSize = $rootSize
            entries    = $files.Count
        }
        boot         = [ordered]@{
            systemCnf   = [ordered]@{
                lba  = $cnf.lba
                size = $cnf.size
                text = $cnfText
            }
            bootExe     = $bootExe
            exe         = [ordered]@{
                lba         = $exe.lba
                size        = $exe.size
                entryPc     = ('0x{0:X8}' -f $entryPc)
                loadAddress = ('0x{0:X8}' -f $loadAddr)
                textSize    = ('0x{0:X}' -f $textSize)
            }
        }
        overlays     = $overlays
        files        = ($files | Select-Object path, lba, size, kind)
    }

    # LF line endings and UTF-8 without a BOM, so the file is byte-stable across
    # platforms and every JSON parser reads it identically. ConvertTo-Json
    # emits CRLF on Windows, which would otherwise differ from the committed blob.
    $json = ($manifest | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
    if (-not $json.EndsWith("`n")) { $json += "`n" }

    if ($Check) {
        if (-not (Test-Path -LiteralPath $Out)) {
            throw "manifest not found: $Out. Run without -Check to generate it."
        }

        # Compare canonically so formatting or key order never causes a false alarm.
        # ReadAllText strips a BOM if one is present, which Get-Content -Raw would not.
        $existing = [System.IO.File]::ReadAllText($Out) | ConvertFrom-Json
        $a = $existing | ConvertTo-Json -Depth 8 -Compress
        $b = $manifest | ConvertTo-Json -Depth 8 -Compress

        if ($a -ne $b) {
            Write-Host ''
            Write-Host 'Manifest does NOT match the disc.' -ForegroundColor Red
            if ($existing.disc.sha256 -ne $sha) {
                Write-Host "  manifest sha256: $($existing.disc.sha256)"
                Write-Host "  disc sha256:     $sha"
                Write-Host '  The disc image is not the one this port targets.'
            }
            else {
                Write-Host '  sha256 matches, so the difference is in the parsed structure.'
            }
            exit 1
        }

        Write-Host "[manifest] OK - matches $Out (sha256 $sha)"
        exit 0
    }

    # UTF-8 without a BOM, so every JSON parser reads it identically.
    [System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[manifest] wrote $Out"
    Write-Host "[manifest] sha256 $sha"
}
finally {
    if ($script:Stream) { $script:Stream.Dispose() }
}
