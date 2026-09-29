[CmdletBinding()]
param([Parameter(Mandatory)][string] $RunDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Report = Get-Content (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
if (-not $Report.passed -or -not $Report.discHashVerified -or
    $Report.failureTracking -ne 'guest-main-bios-and-host-v1') { throw 'unaccepted native run' }
$ExpectedRoute = '90:start,240:cross,300:cross,600:right,1250:cross,1800:start,2100:square,2200:right,2260:square,2320:square,2600:cross,2700:cross,2800:cross'
if ($Report.requested.Input -ne $ExpectedRoute -or $Report.completedFrames -ne 3200) { throw 'unexpected equipment route' }
$States = @(Get-Content (Join-Path $RunDir 'states.json') -Raw | ConvertFrom-Json)
function At([int] $Frame) {
    $Found = @($States | Where-Object frame -eq $Frame)
    if ($Found.Count -ne 1) { throw "missing frame $Frame" }
    return $Found[0]
}
$Before = At 1785
$Opened = At 1830
$Selected = At 2340
$After = At 3200
if ($Before.inventory.open -or -not $Opened.inventory.open -or $After.inventory.open) { throw 'wrong menu lifecycle' }
if ($Before.inventory.slots.Count -ne 10 -or $After.inventory.slots.Count -ne 10) { throw 'incomplete slot evidence' }
if ($Selected.inventory.selection -ne 1 -or $Before.player.weapon -ne $Before.inventory.slots[0].id -or
    $After.player.weapon -ne $Before.inventory.slots[1].id -or $After.player.weapon -eq $Before.player.weapon) {
    throw 'selected weapon was not equipped'
}
for ($i = 0; $i -lt 10; $i++) {
    if ($Before.inventory.slots[$i].id -ne $After.inventory.slots[$i].id -or
        $Before.inventory.slots[$i].quantity -ne $After.inventory.slots[$i].quantity) { throw 'equip changed item contents' }
}
foreach ($Field in @('x','y','z','health')) {
    if ($Before.player.$Field -ne $After.player.$Field) { throw "equip changed player $Field" }
}
$Coverage = Get-Content (Join-Path $RunDir 'coverage.json') -Raw | ConvertFrom-Json
foreach ($Path in @('PSX/PLD/PL04W08.PLW','PSX/SOUND/ARMS08.EDH','PSX/SOUND/ARMS08.VB')) {
    $File = @($Coverage.files | Where-Object Path -eq $Path)
    if ($File.Count -ne 1 -or -not $File[0].ExtentRead) { throw "missing equipped-weapon resource: $Path" }
}
Write-Host 'INVENTORY EQUIP PASS: selected weapon equipped, resources read, ten slots and player state preserved'
Write-Host 'Firing, item use and combination are separate acceptance scenarios.'
