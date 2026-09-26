<#
.SYNOPSIS
    Turns out/probe.json into docs/probe-classification.md.

.DESCRIPTION
    RecompOne's --probe-disc classifies every file on the disc and guesses a load
    base for anything that reads as code. That classification drives the
    recompiler configuration, and the guesses are not all correct, so the result
    is worth archiving as a tracked document rather than leaving it in an
    ignored scratch directory.

    Regenerate out/probe.json first:

        dotnet RecompOne/RecompOne.Recompiler/bin/Release/net10.0/recompone.dll `
            --probe-disc Bio2Nov96.cue -json out/probe.json -all

.PARAMETER Probe
    Path to the probe JSON. Defaults to out/probe.json.

.PARAMETER Out
    Path of the Markdown report. Defaults to docs/probe-classification.md.
#>
[CmdletBinding()]
param(
    [string] $Probe,
    [string] $Out
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Probe) { $Probe = Join-Path $RepoRoot 'out/probe.json' }
if (-not $Out) { $Out = Join-Path $RepoRoot 'docs/probe-classification.md' }

if (-not (Test-Path -LiteralPath $Probe)) {
    throw "probe JSON not found: $Probe. Run --probe-disc first; see this script's help."
}

$p = [System.IO.File]::ReadAllText($Probe) | ConvertFrom-Json
$files = $p.files

$byKind = @{}
foreach ($k in 'executable', 'rawcode', 'media', 'data') {
    $byKind[$k] = @($files | Where-Object { $_.kind -eq $k })
}

$sb = New-Object System.Text.StringBuilder
function Add-Line { param([string] $Text = '') [void]$sb.AppendLine($Text) }

Add-Line '# Disc probe classification'
Add-Line
Add-Line 'Baseline produced by `recompone --probe-disc` against the disc recorded in'
Add-Line '`disc-manifest.json`. This is the evidence the recompiler configuration in'
Add-Line '`port/config/` is built on, so it is regenerated and diffed rather than trusted.'
Add-Line
Add-Line 'Regenerate:'
Add-Line
Add-Line '```powershell'
Add-Line 'dotnet RecompOne/RecompOne.Recompiler/bin/Release/net10.0/recompone.dll ``'
Add-Line '    --probe-disc Bio2Nov96.cue -json out/probe.json -all'
Add-Line 'pwsh -File tools/New-ProbeReport.ps1'
Add-Line '```'
Add-Line
Add-Line '## Summary'
Add-Line
Add-Line '| Kind | Files |'
Add-Line '|---|---|'
foreach ($k in 'executable', 'rawcode', 'media', 'data') {
    Add-Line "| $k | $($byKind[$k].Count) |"
}
Add-Line "| **total files** | **$($files.Count)** |"
Add-Line
Add-Line 'Boot executable resolves to `PSX.EXE`.'
Add-Line

# ---------------------------------------------------------------------------
Add-Line '## Executable'
Add-Line
Add-Line '| Path | LBA | Size | Base | Reason |'
Add-Line '|---|---|---|---|---|'
foreach ($f in $byKind['executable']) {
    $base = if ($f.baseAddress) { $f.baseAddress } else { '-' }
    Add-Line "| ``$($f.path)`` | $($f.lba) | $($f.size) | ``$base`` | $($f.reason) |"
}
Add-Line

# ---------------------------------------------------------------------------
Add-Line '## Code overlays'
Add-Line
Add-Line '| Path | LBA | Size | Guessed base | Self-consistent? |'
Add-Line '|---|---|---|---|---|'
foreach ($f in $byKind['rawcode']) {
    $base = if ($f.baseAddress) { "``$($f.baseAddress)``" } else { '*(none)*' }

    # A base is only credible if the overlay actually fits in free RAM there.
    # The main executable is resident at 0x80010000..0x800BF000 and the stack
    # starts at 0x801FFF00, so an overlay base must fall in 0x800BF000..0x801FFF00.
    $ok = 'yes'
    if (-not $f.baseAddress) { $ok = '**no - cannot be guessed**' }
    else {
        $b = [Convert]::ToInt64($f.baseAddress.Substring(2), 16)
        if ($b -lt [Convert]::ToInt64('800BF000', 16) -or
            ($b + $f.size) -gt [Convert]::ToInt64('801FFF00', 16)) {
            $ok = '**no - overlaps resident code**'
        }
    }
    Add-Line "| ``$($f.path)`` | $($f.lba) | $($f.size) | $base | $ok |"
}
Add-Line
Add-Line '### The guessed bases are not trustworthy'
Add-Line
Add-Line 'Every base above is flagged `baseIsGuess`. Two of them are wrong, and the'
Add-Line 'failure modes are worth naming because they are not obvious from the output:'
Add-Line
Add-Line '- **`STAGE6.BIN` is guessed as `0x8004F000`**, which lies inside the resident main'
Add-Line '  executable (`0x80010000` + `0x0AF000` = `0x800BF000`). An overlay cannot load'
Add-Line '  onto live code, so this base is impossible. `CodeScore.GuessBase` picked it'
Add-Line '  because the overlay references main-executable data far more often (128 pointer'
Add-Line '  hits in that page) than it references itself, and the heuristic takes the'
Add-Line '  most-referenced page as the base.'
Add-Line '- **`TITLE.BIN` gets no base at all.** It contains only 18 in-range pointers,'
Add-Line '  below the function''s `bestHits >= 32` confidence threshold, so it returns 0.'
Add-Line '  `AutoConfigurator` treats a zero base as unanalysable and would silently drop'
Add-Line '  the overlay, leaving the title screen unmapped.'
Add-Line
Add-Line 'The correct base for all seven is **`0x80100000`**, established independently:'
Add-Line
Add-Line '1. The only free region below the stack (`0x801FFF00`) is `0x800BF000`-`0x801FFF00`.'
Add-Line '2. Every overlay''s self-referential pointers land in `0x8010xxxx`-`0x8011Bxxx`, and'
Add-Line '   the largest overlay extent is `0x219B0`, so all seven fit in one slot.'
Add-Line '3. `title` points at `0x80100000`, `0x80101000` and `0x80102000`, all within its own'
Add-Line '   9932-byte image at that base - self-reference, not coincidence.'
Add-Line '4. The extra pointers into `0x8004F000` and `0x800Bxxxx` are the overlays'
Add-Line '   referencing main-executable globals, which is expected and does not imply'
Add-Line '   a base.'
Add-Line
Add-Line 'So `port/config/bio2nov96.json` declares all seven bases explicitly instead of'
Add-Line 'accepting the guesses. That is the whole reason the configuration is hand-curated.'
Add-Line

# ---------------------------------------------------------------------------
Add-Line '## Media'
Add-Line
Add-Line 'Classified by magic number or extension. No action needed beyond leaving them'
Add-Line 'out of the overlay list.'
Add-Line
Add-Line '| Kind | Count |'
Add-Line '|---|---|'
foreach ($g in ($byKind['media'] | Group-Object reason | Sort-Object Count -Descending)) {
    Add-Line "| $($g.Name) | $($g.Count) |"
}
Add-Line
Add-Line '<details><summary>All media files</summary>'
Add-Line
foreach ($f in ($byKind['media'] | Sort-Object path)) {
    Add-Line "- ``$($f.path)`` - $($f.size) bytes - $($f.reason)"
}
Add-Line
Add-Line '</details>'
Add-Line

# ---------------------------------------------------------------------------
Add-Line '## Data'
Add-Line
Add-Line "None of these read as code. Importantly this includes every file the early plan"
Add-Line 'expected to be a false positive risk - `PSX/EMD/*.EMS`, `PSX/ITEM/*.ITP`,'
Add-Line '`PSX/PLD/*.PLD` and `*.PLW`, `PSX/STAGE*/*.RDT` and `*.BSS`, `PSX/SOUND/*.BGM`,'
Add-Line '`PSX/DOOR/*.DO2` - so the configuration needs no pruning beyond excluding them.'
Add-Line
Add-Line '<details><summary>All data files</summary>'
Add-Line
foreach ($f in ($byKind['data'] | Sort-Object path)) {
    Add-Line "- ``$($f.path)`` - $($f.size) bytes"
}
Add-Line
Add-Line '</details>'
Add-Line

Add-Line '---'
Add-Line
Add-Line '## Upstream formatting note'
Add-Line
Add-Line 'The `reason` strings report code-density scores through a culture-sensitive'
Add-Line '`{0:0.00}` format, so on a machine using a comma decimal separator they read'
Add-Line '`(0,92, 388 returns, ...)` rather than `(0.92, 388 returns, ...)`. Cosmetic only;'
Add-Line 'the underlying values are unaffected.'

# LF and no BOM, matching the rest of the repository.
$text = ($sb.ToString()) -replace "`r`n", "`n"
if (-not $text.EndsWith("`n")) { $text += "`n" }
[System.IO.File]::WriteAllText($Out, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[probe-report] wrote $Out"
