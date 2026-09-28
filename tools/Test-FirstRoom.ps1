[CmdletBinding()]
param([string] $OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $RepoRoot ('out/gates/first-room-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$RunDir = Join-Path $OutputRoot 'route'
$HostDll = Join-Path $RepoRoot 'port/RE15pc/bin/Release/net10.0/RE15pc.dll'
$InputRoute = '90:start,240:cross,300:cross,600:up:600,1250:cross,1800:right:17,1840:up:40,1890:square'
function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}
Push-Location $RepoRoot
try {
    & dotnet $HostDll --frames 2700 --timeout 150 --input $InputRoute --trace-input --trace-coverage --verify-audio --verify-overlays --out $RunDir *> (Join-Path $OutputRoot 'route.log')
    Require ($LASTEXITCODE -eq 0) 'native process exit'
    $Report = Get-Content (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
    Require ($Report.passed -and $Report.completedFrames -eq 2700 -and $Report.guestStopped) 'exact stopped-run acceptance'
    $States = @(Get-Content (Join-Path $RunDir 'states.json') -Raw | ConvertFrom-Json)
    $Inputs = @(Get-Content (Join-Path $RunDir 'input.json') -Raw | ConvertFrom-Json)
    function At([int] $Frame) {
        $Found = @($States | Where-Object frame -eq $Frame)
        if ($Found.Count -ne 1) { throw "missing or duplicate frame $Frame" }
        return $Found[0]
    }
    $Start = At 1800
    $Turned = At 1830
    $SlideA = At 1860
    $SlideB = At 1875
    $Action = At 1890
    $Final = $States[-1]
    Require ($Start.roomIndex -eq 0x17 -and $Start.camera -eq 0 -and $Start.overlays -contains 'stage1') 'initial rooftop state'
    Require ($Turned.player.x -eq $Start.player.x -and $Turned.player.y -eq $Start.player.y -and
        $Turned.player.z -eq $Start.player.z -and $Turned.player.yaw -ne $Start.player.yaw) 'turning in place'
    Require ($SlideA.player.z -gt $Start.player.z -and $SlideA.player.x -lt $Start.player.x) 'deliberate forward movement'
    Require ($SlideA.player.z -eq $SlideB.player.z -and $SlideA.player.x -gt $SlideB.player.x -and
        $SlideA.player.y -eq $SlideB.player.y) 'railing collision clamps forward travel while allowing wall sliding'
    Require (@($Inputs | Where-Object { $_.frame -eq 1800 -and $_.buttons -eq '0xFFDF' }).Count -eq 1) 'Right delivered to the guest'
    Require (@($Inputs | Where-Object { $_.frame -eq 1840 -and $_.buttons -eq '0xFFEF' }).Count -eq 1) 'Up delivered to the guest'
    Require (@($Inputs | Where-Object { $_.frame -eq 1890 -and $_.buttons -eq '0x7FFF' }).Count -eq 1) 'Square action delivered to the guest'

    $Manifest = Get-Content (Join-Path $RepoRoot 'disc-manifest.json') -Raw | ConvertFrom-Json
    $Disc = [IO.File]::OpenRead((Join-Path $RepoRoot 'Bio2Nov96.bin'))
    try {
        function Read-DiscBytes($Entry, [int] $Offset, [int] $Count) {
            if ($Offset -lt 0 -or $Count -le 0 -or $Offset + $Count -gt $Entry.size) { throw 'disc slice out of range' }
            $Bytes = [byte[]]::new($Count)
            $Done = 0
            while ($Done -lt $Count) {
                $Logical = $Offset + $Done
                $Sector = [long]$Entry.lba + [long][math]::Floor($Logical / 2048)
                $Within = $Logical % 2048
                $Take = [math]::Min($Count - $Done, 2048 - $Within)
                $null = $Disc.Seek($Sector * 2352 + 24 + $Within, [IO.SeekOrigin]::Begin)
                if ($Disc.Read($Bytes, $Done, $Take) -ne $Take) { throw 'short disc read' }
                $Done += $Take
            }
            return ,$Bytes
        }
        $Origin = $Manifest.files | Where-Object path -eq 'PSX/STAGE1/ROOM1170.RDT'
        $Header = Read-DiscBytes $Origin 0 128
        $Init = [BitConverter]::ToUInt32($Header, 0x40)
        $Offsets = Read-DiscBytes $Origin $Init 2
        $Door = Read-DiscBytes $Origin ($Init + [BitConverter]::ToUInt16($Offsets, 0)) 32
        Require ($Door[0] -eq 0x3b -and $Door[1] -eq 0 -and $Door[2] -eq 2) 'original rooftop door opcode and identity'
        $X = [BitConverter]::ToInt16($Door, 6)
        $Z = [BitConverter]::ToInt16($Door, 8)
        $W = [BitConverter]::ToUInt16($Door, 10)
        $H = [BitConverter]::ToUInt16($Door, 12)
        Require ($Action.player.x -ge $X -and $Action.player.x -lt $X + $W -and
            $Action.player.z -ge $Z -and $Action.player.z -lt $Z + $H) 'action occurs inside the disc-defined exit rectangle'
        $NextX = [BitConverter]::ToInt16($Door, 14)
        $NextY = [BitConverter]::ToInt16($Door, 16)
        $NextZ = [BitConverter]::ToInt16($Door, 18)
        $NextYaw = [BitConverter]::ToUInt16($Door, 20) -band 0xfff
        Require ($Final.roomIndex -eq $Door[23] -and $Final.roomIndex -ne $Start.roomIndex -and
            $Final.camera -eq $Door[24]) 'door reaches its original destination room and camera'
        Require ($Final.player.x -eq $NextX -and $Final.player.y -eq $NextY -and
            $Final.player.z -eq $NextZ -and $Final.player.yaw -eq $NextYaw) 'destination coordinates and facing match the original door record'

        $Destination = 'PSX/STAGE{0}/ROOM{0}{1:X2}0.RDT' -f ($Door[22] + 1), $Door[23]
        $Entry = $Manifest.files | Where-Object path -eq $Destination
        $Expected = Read-DiscBytes $Entry 0 128
        $Ram = [IO.File]::ReadAllBytes((Join-Path $RunDir 'ram-full.bin'))
        $RdtBase = [Convert]::ToUInt32($Final.rdt.Substring(2), 16)
        $RamOffset = $RdtBase -band 0x1fffff
        for ($i = 0; $i -lt 128; $i += 4) {
            $ActualWord = [BitConverter]::ToUInt32($Ram, $RamOffset + $i)
            $ExpectedWord = [BitConverter]::ToUInt32($Expected, $i)
            if ($ActualWord -ne $ExpectedWord -and
                -not ($ActualWord -ge $RdtBase -and ($ActualWord - $RdtBase) -eq $ExpectedWord)) {
                throw "destination RDT header mismatch at offset $i"
            }
        }
        Require ($Ram[0xACAD6] -eq $Door[25]) 'destination floor matches the original door record'
        Write-Host "PASS resident RDT header matches $Destination with relocation"
        @{ passed = $true; revision = $Report.revision; dirty = $Report.dirty; route = $InputRoute;
            start = $Start; turned = $Turned; collision = @($SlideA, $SlideB); action = $Action;
            destination = $Destination; final = $Final; nativeReport = 'route/run.json' } |
            ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputRoot 'first-room.json')
    }
    finally { $Disc.Dispose() }
    Write-Host "FIRST ROOM GATE PASS: $OutputRoot"
}
finally { Pop-Location }
