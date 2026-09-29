#Requires -Version 7.0
<#
.SYNOPSIS
    Converts your disc image into a native sector store, once.

.DESCRIPTION
    After this runs, the port no longer needs a .cue, .bin or .chd at runtime. It
    reads a single compressed file, data/re15pc.disc, which contains the disc's
    track table and its raw 2352-byte sectors - the form the CD layer actually asks
    for, because that is how the guest streams XA audio and MDEC video.

    The store is not a disc image and cannot be turned back into one. It is also the
    entire game, so it is gitignored and must never be committed or shared.

    The conversion itself is done by the host application, not by this script: the
    writer and the reader are the same code, in the same assembly, so the format
    cannot drift between the two.

.PARAMETER Disc
    Your disc: the .cue or .chd. Required.

.PARAMETER Data
    Where to write the store. Defaults to data/re15pc.disc.

.PARAMETER Exe
    Host to invoke. Defaults to the Release build under port/RE15pc, falling back to
    dotnet run.

.EXAMPLE
    pwsh -File tools/Import-Disc.ps1 -Disc .\Bio2Nov96.cue

.EXAMPLE
    pwsh -File tools/Import-Disc.ps1 -Disc D:\dumps\Bio2Nov96.chd -Data D:\re15pc\data
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Disc,

    [string]$Data,
    [string]$Exe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

if (-not $Data) { $Data = Join-Path $root 'data' }

if (-not (Test-Path -LiteralPath $Disc)) {
    throw "disc not found: $Disc`nPass the .cue or .chd of your own copy of the prototype."
}

# Resolve the host. A published build is preferred when it is sitting in dist/,
# because that is what a user who downloaded a release actually has.
$invocation = $null
if ($Exe) {
    if (-not (Test-Path -LiteralPath $Exe)) { throw "host not found: $Exe" }
    $invocation = @{ File = $Exe; Prefix = @() }
}
else {
    $published = Join-Path $root 'dist/RE15pc.exe'
    $builtDll = Join-Path $root 'port/RE15pc/bin/Release/net10.0/RE15pc.dll'
    if (Test-Path -LiteralPath $published) {
        $invocation = @{ File = $published; Prefix = @() }
    }
    elseif (Test-Path -LiteralPath $builtDll) {
        $invocation = @{ File = 'dotnet'; Prefix = @($builtDll) }
    }
    else {
        throw ('no host to run. Build one first:' + "`n" +
               '    dotnet build port/RE15pc/RE15pc.csproj -c Release' + "`n" +
               '    pwsh -File tools/Publish-Exe.ps1')
    }
}

# Values are passed as separate array elements rather than as one pre-quoted
# string: PowerShell quotes native arguments that contain spaces when it builds the
# command line, and adding quotes here instead makes them literal characters, so the
# host receives a path that starts with a double quote and cannot find it.
$arguments = @($invocation.Prefix) + @('--import-disc', $Disc, '--data', $Data)
Write-Host ("running {0} {1}" -f $invocation.File, ($arguments -join ' '))
Write-Host ''

& $invocation.File @arguments
$code = $LASTEXITCODE
if ($code -ne 0) { throw "import failed with exit code $code" }

$store = if (Test-Path -LiteralPath $Data -PathType Container) {
    Join-Path $Data 're15pc.disc'
}
else {
    $Data
}

if (-not (Test-Path -LiteralPath $store)) { throw "import reported success but wrote no store at $store" }

$storeItem = Get-Item -LiteralPath $store
Write-Host ''
Write-Host ("imported {0}" -f $storeItem.FullName)
Write-Host ("  store  {0:N1} MB" -f ($storeItem.Length / 1MB))
Write-Host ("  source {0}" -f $Disc)
Write-Host '  (the host above reports the store size against the disc image, which is the'
Write-Host '   meaningful ratio; the path you passed is usually the few-byte cue sheet)'
Write-Host ''
Write-Host 'The port now boots from the store with no disc image present. Try it:'
Write-Host ("    {0} --frames 180 --out out\store-check" -f $invocation.File)
Write-Host ''
Write-Host 'Do not commit or share the store: it contains the disc.'
