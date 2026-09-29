[CmdletBinding()]
param([Parameter(Mandatory)][string] $RunDir)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Report = Get-Content (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
if (-not $Report.passed -or -not $Report.discHashVerified -or $Report.completedFrames -ne 3500 -or
    $Report.failureTracking -ne 'guest-main-bios-and-host-v1') { throw 'unaccepted firing run' }
$Route = '90:start,240:cross,300:cross,600:right,1250:cross,1800:start,2100:square,2200:right,2260:square,2320:square,2600:cross,2700:cross,2800:cross,3200:r1:60,3260:r1+square:60,3320:r1:30'
if ($Report.requested.Input -ne $Route) { throw 'unexpected firing route' }
$States = @(Get-Content (Join-Path $RunDir 'states.json') -Raw | ConvertFrom-Json)
function At([int] $Frame) {
    $Found = @($States | Where-Object frame -eq $Frame)
    if ($Found.Count -ne 1) { throw "missing frame $Frame" }
    return $Found[0]
}
$Before = At 3195
$Aim = At 3255
$First = At 3270
$Second = At 3330
$After = At 3500
if ($Before.player.weapon -ne 8 -or $After.player.weapon -ne 8 -or $After.player.health -ne $Before.player.health) {
    throw 'weapon or player-health invariant failed'
}
if ($Before.inventory.open -or $Aim.inventory.open -or $After.inventory.open -or
    -not $Aim.inventory.controlBlocked -or $After.inventory.controlBlocked) { throw 'aiming/menu lifecycle mismatch' }
if ($Before.inventory.slots[1].quantity -ne 7 -or $First.inventory.slots[1].quantity -ne 6 -or
    $Second.inventory.slots[1].quantity -ne 5 -or $After.inventory.slots[1].quantity -ne 5) {
    throw 'expected two-shot ammunition consumption was not observed'
}
for ($i = 0; $i -lt 10; $i++) {
    if ($Before.inventory.slots[$i].id -ne $After.inventory.slots[$i].id) { throw 'firing changed an item identity' }
    if ($i -ne 1 -and $Before.inventory.slots[$i].quantity -ne $After.inventory.slots[$i].quantity) {
        throw 'firing changed unrelated inventory'
    }
}
$Coverage = Get-Content (Join-Path $RunDir 'coverage.json') -Raw | ConvertFrom-Json
$Main = $Coverage.functions | Where-Object overlay -eq 'main'
if ($Main.entered -notcontains '0x80066A1C') { throw 'repaired cross-image entry was not exercised' }
$Audio = @($Report.checks | Where-Object Name -eq 'audio')
if ($Audio.Count -ne 1 -or -not $Audio[0].Passed) { throw 'audio output evidence missing' }
Write-Host 'AIMED FIRING PASS: two shots, expected ammunition use, control restored, repaired entry executed'
Write-Host 'Enemy damage and kills require separate within-run evidence.'
