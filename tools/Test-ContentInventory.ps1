[CmdletBinding()]
param([string] $Ledger)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Ledger) { $Ledger = Join-Path $RepoRoot 'docs/content-coverage.json' }
$Manifest = Get-Content (Join-Path $RepoRoot 'disc-manifest.json') -Raw | ConvertFrom-Json
$Data = Get-Content -LiteralPath $Ledger -Raw | ConvertFrom-Json
if ($Data.discSha256 -ne $Manifest.disc.sha256) { throw 'wrong disc identity' }
$Expected = @{}
foreach ($File in $Manifest.files | Where-Object kind -eq 'file') { $Expected[$File.path] = $File }
$Seen = @{}
foreach ($File in $Data.files) {
    if ($Seen.ContainsKey($File.path)) { throw "duplicate file: $($File.path)" }
    if (-not $Expected.ContainsKey($File.path)) { throw "unexpected file: $($File.path)" }
    if ($File.lba -ne $Expected[$File.path].lba -or $File.size -ne $Expected[$File.path].size) {
        throw "wrong extent: $($File.path)"
    }
    if ($File.status -notin @('unverified','partial','verified','original-limit')) { throw 'invalid coverage status' }
    if ([string]::IsNullOrWhiteSpace($File.category) -or $File.category -eq 'unclassified') { throw 'unclassified file' }
    if ($File.status -ne 'unverified' -and $File.evidence.Count -eq 0) { throw 'claimed coverage without evidence' }
    $Seen[$File.path] = $true
}
if ($Seen.Count -ne $Expected.Count) { throw 'inventory omits disc files' }
foreach ($Map in $Data.functionMaps) {
    $Source = Get-Content (Join-Path $RepoRoot $Map.map) -Raw | ConvertFrom-Json
    if ($Source.functions.Count -ne $Map.mapEntries) { throw "stale function-map count: $($Map.overlay)" }
}
foreach ($Run in $Data.runs) {
    $ReportPath = Join-Path $RepoRoot ($Run.path + '/run.json')
    $CoveragePath = Join-Path $RepoRoot ($Run.path + '/coverage.json')
    if (-not (Test-Path $ReportPath) -or -not (Test-Path $CoveragePath)) {
        throw "local run evidence unavailable; reproduce it: $($Run.path)"
    }
    $Report = Get-Content $ReportPath -Raw | ConvertFrom-Json
    if (-not $Report.passed -or $Report.revision -ne $Run.revision -or $Report.discSha256 -ne $Data.discSha256 -or
        $Report.failureTracking -ne 'guest-main-bios-and-host-v1') {
        throw 'run evidence disagrees with ledger provenance'
    }
}
Write-Host "INVENTORY CONSISTENCY PASS: $($Seen.Count) files accounted for"
Write-Host 'This is an inventory check, not the full-content acceptance gate.'
