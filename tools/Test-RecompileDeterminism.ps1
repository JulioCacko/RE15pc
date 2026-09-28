<#
.SYNOPSIS
    Verifies that the recompiler produces byte-identical output for the same input.

.DESCRIPTION
    The recompiled C# under generated/ is a pure function of the disc image and
    port/config/bio2nov96.json. That property is what makes a behavioural change
    attributable: if two runs of the same configuration can differ, then any later
    difference in the game's behaviour could be recompiler drift rather than
    something the port did.

    It also decides whether generated/ may stay out of version control. It is
    ignored on the grounds that it can be reproduced exactly; if that stops being
    true, it has to be committed instead.

    This is the Phase 5 gate recorded in docs/phases.md. Run it after any change to
    the recompiler configuration, to the function maps, or to a patch in patches/.

.PARAMETER Config
    Recompiler configuration. Defaults to port/config/bio2nov96.json.

.PARAMETER Recompiler
    Path to the built recompiler assembly. Defaults to the Release build of
    RecompOne.Recompiler, which bootstrap.ps1 produces.

.EXAMPLE
    pwsh -File tools/Test-RecompileDeterminism.ps1

.NOTES
    Rewrites generated/ with the second run's output, so a failure leaves the tree
    holding the differing build. That is deliberate: it is the artifact needed to
    investigate the difference.
#>
[CmdletBinding()]
param(
    [string] $Config,
    [string] $Recompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Config) { $Config = Join-Path $RepoRoot 'port/config/bio2nov96.json' }
if (-not $Recompiler) {
    $Recompiler = Join-Path $RepoRoot 'RecompOne/RecompOne.Recompiler/bin/Release/net10.0/recompone.dll'
}

if (-not (Test-Path -LiteralPath $Config)) { throw "config not found: $Config" }
if (-not (Test-Path -LiteralPath $Recompiler)) {
    throw "recompiler not found: $Recompiler`nRun bootstrap.ps1, or build RecompOne.sln in Release."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet not found on PATH" }

$Generated = Join-Path $RepoRoot 'generated'
if (-not (Test-Path -LiteralPath $Generated)) {
    throw "generated/ not found: $Generated`nRun the recompiler once first."
}

function Get-OutputHashes {
    $map = @{}
    foreach ($f in Get-ChildItem -LiteralPath $Generated -File) {
        $map[$f.Name] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    return $map
}

Write-Host "[determinism] hashing the existing generated/ output"
$before = Get-OutputHashes
if ($before.Count -eq 0) { throw "generated/ is empty; nothing to compare" }
Write-Host "[determinism] $($before.Count) file(s)"

Write-Host "[determinism] recompiling with the same configuration"
$log = & dotnet $Recompiler $Config 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host ($log | Select-Object -Last 20 | Out-String)
    throw "the recompiler failed with exit code $LASTEXITCODE"
}

Write-Host "[determinism] comparing"
$after = Get-OutputHashes
$differences = New-Object System.Collections.Generic.List[string]

foreach ($name in $before.Keys) {
    if (-not $after.ContainsKey($name)) {
        $differences.Add("missing after recompile: $name")
    }
    elseif ($before[$name] -ne $after[$name]) {
        $differences.Add("differs: $name`n    before $($before[$name])`n    after  $($after[$name])")
    }
}
foreach ($name in $after.Keys) {
    if (-not $before.ContainsKey($name)) { $differences.Add("new after recompile: $name") }
}

if ($differences.Count -gt 0) {
    Write-Host ''
    Write-Host "[determinism] FAIL - the recompiler is not deterministic for this input" -ForegroundColor Red
    foreach ($d in $differences) { Write-Host "  $d" }
    Write-Host ''
    Write-Host 'Two runs of one configuration produced different output. Until that is fixed, a change in'
    Write-Host 'the game behaviour cannot be attributed to the port, and generated/ must be committed'
    Write-Host 'rather than ignored. See docs/phases.md, Phase 5.'
    exit 1
}

Write-Host ''
Write-Host "[determinism] PASS - all $($before.Count) file(s) byte-identical across two recompiles"
exit 0
