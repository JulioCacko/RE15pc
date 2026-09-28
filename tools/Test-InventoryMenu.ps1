[CmdletBinding()]
param([Parameter(Mandatory)][string] $OpenRun, [Parameter(Mandatory)][string] $ClosedRun)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$OpenReport = Get-Content (Join-Path $OpenRun 'run.json') -Raw | ConvertFrom-Json
$ClosedReport = Get-Content (Join-Path $ClosedRun 'run.json') -Raw | ConvertFrom-Json
foreach ($Report in @($OpenReport, $ClosedReport)) {
    if (-not $Report.passed -or $Report.failureTracking -ne 'guest-main-bios-and-host-v1' -or -not $Report.discHashVerified) {
        throw 'unaccepted native run'
    }
}
$Prefix = '90:start,240:cross,300:cross,600:right,1250:cross,1800:start'
if ($OpenReport.requested.Input -ne $Prefix -or $ClosedReport.requested.Input -ne ($Prefix + ',2100:cross')) {
    throw 'unexpected inventory input route'
}
$Opened = [IO.File]::ReadAllBytes((Join-Path $OpenRun 'ram-full.bin'))
$Closed = [IO.File]::ReadAllBytes((Join-Path $ClosedRun 'ram-full.bin'))
if (([BitConverter]::ToUInt32($Opened, 0xACA3C) -band 0x40) -eq 0) { throw 'inventory did not open' }
if (([BitConverter]::ToUInt32($Closed, 0xACA3C) -band 0x40) -ne 0) { throw 'inventory did not close' }
for ($i = 0; $i -lt 40; $i++) {
    if ($Opened[0xB10AC + $i] -ne $Closed[0xB10AC + $i]) { throw 'menu open/close changed inventory slots' }
}
foreach ($Offset in @(0xACA88,0xACA8C,0xACA90)) {
    if ([BitConverter]::ToInt32($Opened, $Offset) -ne [BitConverter]::ToInt32($Closed, $Offset)) {
        throw 'menu open/close moved the player'
    }
}
Write-Host 'INVENTORY OPEN/CLOSE PASS: native runs, menu flag, ten unchanged slots, unchanged position'
Write-Host 'Item selection, equipment, use and combination are separate gates and remain unverified.'
