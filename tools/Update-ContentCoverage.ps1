[CmdletBinding()]
param([string[]] $RunDir = @(), [string] $Ledger)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Ledger) { $Ledger = Join-Path $RepoRoot 'docs/content-coverage.json' }
$Manifest = Get-Content (Join-Path $RepoRoot 'disc-manifest.json') -Raw | ConvertFrom-Json
$Previous = @{}
$Runs = @{}
if (Test-Path -LiteralPath $Ledger) {
    $Old = Get-Content -LiteralPath $Ledger -Raw | ConvertFrom-Json
    if ($Old.discSha256 -ne $Manifest.disc.sha256) { throw 'ledger belongs to another disc' }
    foreach ($File in $Old.files) { $Previous[$File.path] = $File }
    foreach ($Run in $Old.runs) { $Runs[$Run.path] = $Run }
}
$Categories = @{
    EXE='resident-code'; BIN='overlay-code'; RDT='room-data-and-scripts'; BSS='background-container'
    PLD='character-model-and-animation'; PLW='weapon-model-and-animation'; EMS='model-container'
    ITP='item-container'; DO2='door-container'; TIM='texture'; TM2='texture-container'; PIX='image-data'
    STR='movie-container'; BGM='music-sequence-container'; EDH='sound-bank-header'; VB='sound-sample-container'
    ESP='effect-container'; SCD='script'; CNF='boot-configuration'; DAT='data'
}
$Files = foreach ($Entry in $Manifest.files | Where-Object kind -eq 'file' | Sort-Object path) {
    $Extension = [IO.Path]::GetExtension($Entry.path).TrimStart('.').ToUpperInvariant()
    $Category = if ($Categories.ContainsKey($Extension)) { $Categories[$Extension] } else { 'unclassified' }
    $Status = 'unverified'; $Evidence = @(); $Notes = ''
    if ($Previous.ContainsKey($Entry.path)) {
        $Prior = $Previous[$Entry.path]
        if ($Prior.lba -ne $Entry.lba -or $Prior.size -ne $Entry.size) { throw "changed extent: $($Entry.path)" }
        $Status = $Prior.status; $Evidence = @($Prior.evidence); $Notes = $Prior.notes
    }
    [pscustomobject][ordered]@{ path=$Entry.path; category=$Category; lba=$Entry.lba; size=$Entry.size;
        status=$Status; evidence=$Evidence; notes=$Notes }
}
$ByPath = @{}
foreach ($File in $Files) { $ByPath[$File.path] = $File }
foreach ($Directory in $RunDir) {
    $Directory = [IO.Path]::GetFullPath($Directory)
    $Report = Get-Content (Join-Path $Directory 'run.json') -Raw | ConvertFrom-Json
    if ($Report.PSObject.Properties.Name -notcontains 'failureTracking' -or
        $Report.failureTracking -ne 'guest-main-bios-and-host-v1') {
        throw "legacy failure tracking; rerun before importing: $Directory"
    }
    $Capture = @($Report.checks | Where-Object Name -eq 'coverage-capture')
    if (-not $Report.passed -or -not $Report.discHashVerified -or
        $Report.discSha256 -ne $Manifest.disc.sha256 -or $Capture.Count -ne 1 -or -not $Capture[0].Passed) {
        throw "unaccepted or unidentified coverage run: $Directory"
    }
    $Coverage = Get-Content (Join-Path $Directory 'coverage.json') -Raw | ConvertFrom-Json
    $ConsoleLog = Get-Content (Join-Path $Directory 'console.log') -Raw
    if ($ConsoleLog -match 'runtime has crashed|unmapped call:|\[Runtime\] thread \d+ stopped:|\[Host\] event processing failed:') {
        throw "fault found in a purportedly passing run: $Directory"
    }
    if ($Coverage.unregisteredEntries.Count -ne 0) { throw 'unregistered entry evidence' }
    $Relative = [IO.Path]::GetRelativePath($RepoRoot, $Directory).Replace('\','/')
    $Runs[$Relative] = [ordered]@{ path=$Relative; revision=$Report.revision; dirty=$Report.dirty;
        failureTracking=$Report.failureTracking;
        frames=$Report.completedFrames; input=$Report.requested.Input; configHashes=$Report.configHashes;
        functions=@($Coverage.functions | ForEach-Object {
            [ordered]@{ overlay=$_.overlay; registered=$_.registered; entered=$_.entered.Count; unentered=$_.unentered.Count }
        }) }
    foreach ($Observed in $Coverage.files | Where-Object TouchedSectors -gt 0) {
        if (-not $ByPath.ContainsKey($Observed.Path)) { throw "file outside the inventory: $($Observed.Path)" }
        $File = $ByPath[$Observed.Path]
        if ($File.lba -ne $Observed.Lba -or $File.size -ne $Observed.Size) { throw 'coverage extent mismatch' }
        if ($File.status -eq 'unverified') { $File.status = 'partial' }
        $File.evidence = @(@($File.evidence) + "$Relative/coverage.json" | Sort-Object -Unique)
    }
}
$MapCounts = foreach ($Map in Get-ChildItem (Join-Path $RepoRoot 'port/config/funcmaps/*.json') | Sort-Object Name) {
    $Data = Get-Content $Map.FullName -Raw | ConvertFrom-Json
    [ordered]@{ overlay=$Map.BaseName; mapEntries=$Data.functions.Count;
        map=('port/config/funcmaps/' + $Map.Name) }
}
$Result = [ordered]@{
    schemaVersion=1; discSha256=$Manifest.disc.sha256
    definitions=[ordered]@{
        unverified='No qualifying evidence yet.'
        partial='Observed access or limited scenario evidence; not complete playability.'
        verified='Requires explicit scenario assertions for the full entry scope.'
        'original-limit'='Requires original-code/data evidence, not an unexplained port failure.'
    }
    openScopes=@('Conditional branches and path combinations are not covered by function-entry hits.',
        'Model block boundaries are indexed, but semantic identities and animation, item, sample, sequence, background and script-branch coverage remain open.',
        'Reads do not establish decoding, presentation, interaction or playability.',
        'Runs with different config hashes remain separate evidence; do not merge them into a release verdict.')
    modelInventory='docs/model-inventory.json'
    functionMaps=@($MapCounts); runs=@($Runs.Values | Sort-Object path); files=@($Files)
}
$Result | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $Ledger
$Groups = $Files | Group-Object status | Sort-Object Name
Write-Host "Coverage ledger: $($Files.Count) files, $($Runs.Count) traced runs"
foreach ($Group in $Groups) { Write-Host "  $($Group.Name): $($Group.Count)" }
Write-Host 'This command updates observations; it cannot mark an entry verified.'
