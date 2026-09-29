[CmdletBinding()]
param([Parameter(Mandatory)][string] $RunDir, [switch] $TestRejections)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Report = Get-Content (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
$States = @(Get-Content (Join-Path $RunDir 'states.json') -Raw | ConvertFrom-Json)
$Coverage = Get-Content (Join-Path $RunDir 'coverage.json') -Raw | ConvertFrom-Json
$Inputs = @(Get-Content (Join-Path $RunDir 'input.json') -Raw | ConvertFrom-Json)

function Assert-Damage($Report, $States, $Coverage, $Inputs) {
    $Route = '90:start,240:cross,300:cross,600:right,1250:cross,1800:start,2100:square,2200:right,2260:square,2320:square,2600:cross,2700:cross,2800:cross,3200:r1:60,3260:r1+square:420'
    if (-not $Report.passed -or -not $Report.guestStopped -or -not $Report.discHashVerified -or
        $Report.failureTracking -ne 'guest-main-bios-and-host-v1' -or $Report.findings.Failed -or
        $Report.completedFrames -ne 4000 -or $Report.requested.Frames -ne 4000 -or
        $Report.requested.Input -ne $Route) { throw 'unaccepted damage route' }
    foreach ($Name in 'completion','runtime','preservation','artifacts','audio','coverage-capture') {
        $Check = @($Report.checks | Where-Object Name -eq $Name)
        if ($Check.Count -ne 1 -or -not $Check[0].Passed) { throw "missing passing check: $Name" }
    }
    foreach ($Expected in @(@(3200,'0xF7FF','0xFFF7'), @(3260,'0x77FF','0xFF77'), @(3680,'0xFFFF','0xFFFF'))) {
        $InputSample = @($Inputs | Where-Object frame -eq $Expected[0])
        if ($InputSample.Count -ne 1 -or $InputSample[0].buttons -ne $Expected[1] -or
            $InputSample[0].biosButtons -ne $Expected[2]) { throw 'missing delivered combat input' }
    }
    $Frames = @(3195..3990 | Where-Object { $_ % 15 -eq 0 }) + @(4000)
    $Samples = foreach ($Frame in $Frames) {
        $Found = @($States | Where-Object frame -eq $Frame)
        if ($Found.Count -ne 1) { throw "missing or duplicate frame $Frame" }
        $Sample = $Found[0]
        if ($Sample.enemies.slots.Count -ne 20) { throw 'incomplete enemy pool' }
        for ($i = 0; $i -lt 20; $i++) {
            if ($Sample.enemies.slots[$i].slot -ne $i) { throw 'misordered enemy pool' }
        }
        if ($Sample.player.character -ne 4 -or $Sample.player.weapon -ne 8 -or
            $Sample.player.health -ne 100 -or $Sample.roomIndex -ne 3 -or $Sample.inventory.open) {
            throw 'combat scenario changed'
        }
        $Sample
    }
    $Before = $Samples[0]
    $First = $Samples | Where-Object frame -eq 3270
    $Reset = $Samples | Where-Object frame -eq 3420
    $After = $Samples[-1]
    if ($Before.inventory.slots[1].quantity -ne 7 -or $First.inventory.slots[1].quantity -ne 6 -or
        $Reset.inventory.slots[1].quantity -ne 2 -or $After.inventory.slots[1].quantity -ne 0) {
        throw 'missing shot consumption'
    }
    foreach ($Slot in 0..5) {
        $Original = $Before.enemies.slots[$Slot]
        if ($Original.kind -ne 22 -or $Original.spawnId -ne $Slot -or
            ($Original.flags -band 1) -eq 0 -or $Original.health -le 0) { throw 'missing original actor' }
        foreach ($Sample in $Samples) {
            $Actor = $Sample.enemies.slots[$Slot]
            if ($Actor.kind -ne $Original.kind -or $Actor.spawnId -ne $Original.spawnId) {
                throw 'original actor identity changed'
            }
        }
    }
    foreach ($Slot in 1,2,4,5) {
        if ($First.enemies.slots[$Slot].health -ge $Before.enemies.slots[$Slot].health) {
            throw 'missing within-run damage'
        }
    }
    # Negative health is insufficient: these original actors reset to 30 and stay active.
    foreach ($Slot in 1,2,4) {
        if ($Reset.enemies.slots[$Slot].health -ne 30 -or ($Reset.enemies.slots[$Slot].flags -band 1) -eq 0) {
            throw 'missing health-reset distinction'
        }
    }
    foreach ($Sample in $Samples | Where-Object frame -ge 3540) {
        foreach ($Slot in 0,5) {
            $Actor = $Sample.enemies.slots[$Slot]
            if ($Actor.health -ge 0 -or $Actor.flags -ne 0 -or $Actor.state -ne 0x2060107) {
                throw 'original actor removal did not persist'
            }
        }
    }
    foreach ($Slot in 6,7) {
        $Actor = $After.enemies.slots[$Slot]
        if (($Before.enemies.slots[$Slot].flags -band 1) -ne 0 -or $Actor.spawnId -ne $Slot -or
            $Actor.kind -ne 22 -or ($Actor.flags -band 1) -eq 0 -or $Actor.health -le 0) {
            throw 'missing replacement actor accounting'
        }
    }
    foreach ($Expected in @(@('stage1','0x80109554'), @('main','0x80039A74'), @('main','0x8004267C'))) {
        $Image = @($Coverage.functions | Where-Object overlay -eq $Expected[0])
        if ($Image.Count -ne 1 -or $Image[0].entered -notcontains $Expected[1]) { throw 'missing removal handler execution' }
    }
}

Assert-Damage $Report $States $Coverage $Inputs
if ($TestRejections) {
    # Mutate only in memory; never rewrite native captures or touch saves.
    $Cases = @(
        @{ Name = 'no damage'; Error = 'missing within-run damage'; Edit = { param($s)
            $Before = $s | Where-Object frame -eq 3195
            $First = $s | Where-Object frame -eq 3270
            foreach ($i in 1,2,4,5) { $First.enemies.slots[$i].health = $Before.enemies.slots[$i].health } } },
        @{ Name = 'still active'; Error = 'original actor removal did not persist'; Edit = { param($s)
            ($s | Where-Object frame -eq 4000).enemies.slots[0].flags = 1 } },
        @{ Name = 'slot reused'; Error = 'original actor identity changed'; Edit = { param($s)
            ($s | Where-Object frame -eq 3540).enemies.slots[0].spawnId = 8 } },
        @{ Name = 'truncated pool'; Error = 'incomplete enemy pool'; Edit = { param($s)
            $s[-1].enemies.slots = @($s[-1].enemies.slots | Select-Object -First 6) } },
        @{ Name = 'no ammunition use'; Error = 'missing shot consumption'; Edit = { param($s)
            $s[-1].inventory.slots[1].quantity = 7 } }
    )
    foreach ($Case in $Cases) {
        $Copy = @($States | ConvertTo-Json -Depth 20 | ConvertFrom-Json)
        & $Case.Edit $Copy
        $Rejected = $false
        try { Assert-Damage $Report $Copy $Coverage $Inputs }
        catch { if ($_.Exception.Message -ne $Case.Error) { throw }; $Rejected = $true }
        if (-not $Rejected) { throw "false pass: $($Case.Name)" }
    }
    Write-Host "REJECTION CHECKS PASS: $($Cases.Count) falsified evidence cases"
}
Write-Host 'DAMAGE/REMOVAL PASS: same-run damage, two original actors removed, health resets and new actors distinguished'
Write-Host 'This scenario does not certify a cleared encounter, all enemy types, or all weapons.'
