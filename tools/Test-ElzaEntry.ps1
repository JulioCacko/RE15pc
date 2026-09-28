[CmdletBinding()]
param([string] $OutputRoot, [switch] $VerifyOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $RepoRoot ('out/gates/elza-entry-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$RunDir = Join-Path $OutputRoot 'route'
function Require([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}
Push-Location $RepoRoot
try {
    $Route = '90:start,240:cross,300:cross,600:right,1250:cross'
    if (-not $VerifyOnly) {
        & dotnet port/RE15pc/bin/Release/net10.0/RE15pc.dll --frames 2100 --timeout 130 --input $Route --trace-input --trace-coverage --verify-audio --verify-overlays --out $RunDir *> (Join-Path $OutputRoot 'route.log')
        Require ($LASTEXITCODE -eq 0) 'native Elza process exit'
    }
    $Report = Get-Content (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
    Require ($Report.passed -and $Report.completedFrames -eq 2100 -and $Report.requested.Input -eq $Route -and
        $Report.failureTracking -eq 'guest-main-bios-and-host-v1') 'strict entry acceptance with guest-thread failure tracking'
    $States = @(Get-Content (Join-Path $RunDir 'states.json') -Raw | ConvertFrom-Json)
    $Last = $States[-1]
    Require ($Last.player.character -eq 4 -and $Last.player.health -gt 0 -and
        $Last.roomIndex -eq 3 -and $Last.overlays -contains 'stage1') 'Elza is alive in the expected starting room'
    $Coverage = Get-Content (Join-Path $RunDir 'coverage.json') -Raw | ConvertFrom-Json
    $Stage = $Coverage.functions | Where-Object overlay -eq 'stage1'
    Require ($Stage.entered -contains '0x80100424') 'previously missing enemy callback executes'
    $Main = $Coverage.functions | Where-Object overlay -eq 'main'
    Require ($Main.entered -contains '0x8001031C') 'previously missing computed-copy suffix executes'
    $Manifest = Get-Content disc-manifest.json -Raw | ConvertFrom-Json
    $Entry = $Manifest.files | Where-Object path -eq 'PSX/STAGE1/ROOM1031.RDT'
    $Expected = [byte[]]::new(128)
    $Disc = [IO.File]::OpenRead((Join-Path $RepoRoot 'Bio2Nov96.bin'))
    try {
        $null = $Disc.Seek([long]$Entry.lba * 2352 + 24, [IO.SeekOrigin]::Begin)
        if ($Disc.Read($Expected, 0, $Expected.Length) -ne $Expected.Length) { throw 'short RDT header read' }
        function Read-Word([int] $Logical) {
            if ($Logical -lt 0 -or $Logical % 4 -ne 0 -or $Logical + 4 -gt $Entry.size) { throw 'invalid room pointer' }
            $Sector = [long]$Entry.lba + [long][math]::Floor($Logical / 2048)
            $WordBytes = [byte[]]::new(4)
            $null = $Disc.Seek($Sector * 2352 + 24 + ($Logical % 2048), [IO.SeekOrigin]::Begin)
            if ($Disc.Read($WordBytes, 0, 4) -ne 4) { throw 'short room word read' }
            return [BitConverter]::ToUInt32($WordBytes, 0)
        }
        Require ($Last.camera -lt $Expected[1]) 'camera index belongs to the original room'
        # Guest 0x800392D4 selects RID[camera].mask and 0x80039358 writes
        # its high-word sprite count to RDT[0]; the -1 sentinel writes zero.
        $CameraTable = [BitConverter]::ToUInt32($Expected, 0x24)
        $MaskOffset = Read-Word ($CameraTable + $Last.camera * 32 + 0x1c)
        $MaskHeader = Read-Word $MaskOffset
        $Expected[0] = if ($MaskHeader -eq [uint32]::MaxValue) { 0 } else { ($MaskHeader -shr 16) -band 255 }
    }
    finally { $Disc.Dispose() }
    $Ram = [IO.File]::ReadAllBytes((Join-Path $RunDir 'ram-full.bin'))
    $Base = [Convert]::ToUInt32($Last.rdt.Substring(2), 16)
    $Offset = $Base -band 0x1fffff
    for ($i = 0; $i -lt 128; $i += 4) {
        $Actual = [BitConverter]::ToUInt32($Ram, $Offset + $i)
        $Word = [BitConverter]::ToUInt32($Expected, $i)
        if ($Actual -ne $Word -and -not ($Actual -ge $Base -and ($Actual - $Base) -eq $Word)) {
            throw "Elza RDT header mismatch at $i"
        }
    }
    Write-Host 'PASS resident header matches ROOM1031.RDT with relocation and the original sprite-count update'
    @{ passed=$true; revision=$Report.revision; dirty=$Report.dirty; route=$Route;
        room='PSX/STAGE1/ROOM1031.RDT'; final=$Last; nativeReport='route/run.json' } |
        ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputRoot 'elza-entry.json')
    Write-Host "ELZA ENTRY GATE PASS: $OutputRoot"
}
finally { Pop-Location }
