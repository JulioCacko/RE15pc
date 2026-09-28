[CmdletBinding()]
param([string] $OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $RepoRoot ('out/gates/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$HostDll = Join-Path $RepoRoot 'port/RE15pc/bin/Release/net10.0/RE15pc.dll'
$ChecksDll = Join-Path $RepoRoot 'tests/RE15pc.Checks/bin/Release/net10.0/RE15pc.Checks.dll'
Push-Location $RepoRoot
try {
    & dotnet $ChecksDll *> (Join-Path $OutputRoot 'unit.log')
    if ($LASTEXITCODE -ne 0) { throw 'acceptance unit checks failed' }
    function Invoke-Case([string] $Name, [int] $ExpectedExit, [string[]] $RunArgs) {
        $RunDir = Join-Path $OutputRoot $Name
        & dotnet $HostDll --out $RunDir @RunArgs *> (Join-Path $OutputRoot "$Name.log")
        $Code = $LASTEXITCODE
        if ($Code -ne $ExpectedExit) { throw "$Name exit=$Code expected=$ExpectedExit" }
        $Report = Get-Content (Join-Path $RunDir 'run.json') -Raw | ConvertFrom-Json
        if ($Report.passed -ne ($ExpectedExit -eq 0)) { throw "$Name JSON disagrees with exit" }
        $Verdict = (Get-Content (Join-Path $RunDir 'verdict.txt') -Raw).Trim()
        if ($Verdict -ne $(if ($ExpectedExit -eq 0) { 'PASS' } else { 'FAIL' })) {
            throw "$Name verdict disagrees with exit"
        }
        Write-Host "PASS $Name exit=$Code frames=$($Report.completedFrames)"
        return $Report
    }
    $First = Invoke-Case 'title-a' 0 @('--frames','180','--timeout','45','--input','0:none','--verify-overlays')
    $Second = Invoke-Case 'title-b' 0 @('--frames','180','--timeout','45','--input','0:none','--verify-overlays')
    if ($First.completedFrames -ne 180 -or $Second.completedFrames -ne 180) { throw 'frame boundary mismatch' }
    $Silent = Invoke-Case 'silent' 1 @('--frames','1','--timeout','45','--input','0:none','--verify-audio')
    if (($Silent.checks | Where-Object Name -eq 'audio').Passed) { throw 'silent check unexpectedly passed' }
    $Timed = Invoke-Case 'timeout' 1 @('--frames','100000','--timeout','1','--input','0:none')
    if (($Timed.checks | Where-Object Name -eq 'completion').Passed) { throw 'timeout completion passed' }
    & dotnet $HostDll --out (Join-Path $OutputRoot 'title-a') --frames 180 *> (Join-Path $OutputRoot 'reuse.log')
    if ($LASTEXITCODE -ne 2) { throw 'nonempty output directory was accepted' }
    Write-Host "ACCEPTANCE HARNESS PASS: $OutputRoot"
}
finally { Pop-Location }
