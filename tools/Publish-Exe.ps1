#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes RE15pc as a self-contained Windows executable.

.DESCRIPTION
    `dotnet run --project port/RE15pc` is the development path: it needs the SDK,
    the pinned RecompOne clone and the generated tree present on the machine that
    runs it. This script produces the other thing: a distributable that launches
    by double-click on a Windows x64 box with no .NET installed.

    The default is a self-contained *folder* publish, and that is deliberate
    rather than a shortcut:

      PublishSingleFile=false    Two dependencies of this host do not survive
                                 single-file packing, and both fail at runtime
                                 rather than at build time.

                                 ImGui.NET locates cimgui.dll through
                                 Assembly.Location, which is empty for an
                                 assembly embedded in a single-file bundle. The
                                 host then calls into an unloaded native library
                                 and dies with 0xC0000005. It surfaces late, in
                                 HostWindow.OnClosing -> ConfigManager.SaveView
                                 -> igSaveIniSettingsToMemory, which means the
                                 game itself runs and the process only crashes on
                                 the way out - so a single-file build looks
                                 healthy right up until it corrupts its own exit.

                                 MonoMod.RuntimeDetour, which RecompOne uses for
                                 mod hooks, declares single-file unsupported and
                                 the SDK emits an explicit warning saying so.

                                 Verified 2026-02 against SDL 2.30.8 / net10.0:
                                 PublishSingleFile=true aborts with 0xC0000005,
                                 PublishSingleFile=false completes and passes the
                                 180-frame gate.

      PublishTrimmed=false       The recompiled guest is ~98 MB of generated C#
                                 reached through dispatch tables, and RecompOne
                                 resolves overlays and mods reflectively. Trimming
                                 would delete code that looks unreferenced and
                                 break dispatch at runtime.

      PublishReadyToRun=false    Keeps the build reproducible and avoids
                                 crossgen2 churn on a 98 MB assembly.

    Native dependencies (glfw3.dll, cimgui.dll, soft_oal.dll, SDL2.dll, nfd.dll)
    are copied into the standard runtimes/win-x64/native layout beside the exe,
    where both .NET and ImGui.NET's own loader expect to find them.

    The result still needs the user's own disc image beside it, because the disc
    is copyrighted and never redistributed; see NOTICE and README.md.

.PARAMETER RuntimeIdentifier
    Target RID. Windows x64 is the gold contract's first target. win-arm64
    publishes and is untested.

.PARAMETER OutputDirectory
    Publish destination. Defaults to <repo root>/dist.

.PARAMETER SingleFile
    Bundle everything into one RE15pc.exe. Known to crash at shutdown for the
    reasons above; use it only to reproduce that defect.

.PARAMETER NoPrune
    Keep the publish directory as-is instead of removing a previous publish first.

.EXAMPLE
    pwsh -File tools/Publish-Exe.ps1

.EXAMPLE
    pwsh -File tools/Publish-Exe.ps1 -RuntimeIdentifier win-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$OutputDirectory,

    [switch]$SingleFile,
    [switch]$NoPrune
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }

$project = Join-Path $root 'port/RE15pc/RE15pc.csproj'
$icon = Join-Path $root 'assets/icon.ico'
$cue = Join-Path $root 'Bio2Nov96.cue'
$disc = Join-Path $root 'Bio2Nov96.bin'

if (-not (Test-Path -LiteralPath $project)) { throw "project not found: $project" }
if (-not (Test-Path -LiteralPath $icon)) {
    throw "assets/icon.ico is missing; run tools/New-AppIcon.ps1 first"
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'generated/main.cs'))) {
    throw ('generated/ is missing. The recompiled guest is compiled into this project, ' +
           'so run bootstrap.ps1 and the recompiler first; see README.md.')
}

# A stale publish would leave DLLs behind that the new build does not use, which
# makes the output look complete when it is not.
if (-not $NoPrune -and (Test-Path -LiteralPath $OutputDirectory)) {
    Write-Host "clearing $OutputDirectory"
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}

$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    "-p:PublishSingleFile=$($SingleFile.IsPresent.ToString().ToLowerInvariant())",
    '-o', $OutputDirectory
)
if ($SingleFile) {
    Write-Warning 'Single-file packaging is known to crash this host at shutdown (ImGui.NET native resolution). Reproducing that defect only.'
    $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}

Write-Host ('dotnet ' + ($publishArgs -join ' '))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $OutputDirectory 'RE15pc.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "publish produced no RE15pc.exe in $OutputDirectory" }

# The exe is only useful with a disc beside it. Copy the cue sheet, and the disc
# itself when it is local, so the output directory is directly runnable. Neither
# is ever committed; both are gitignored.
$copied = [System.Collections.Generic.List[string]]::new()
foreach ($file in @($cue, $disc)) {
    if (Test-Path -LiteralPath $file) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $OutputDirectory (Split-Path -Leaf $file)) -Force
        $copied.Add((Split-Path -Leaf $file))
    }
}
# Player state and host settings are machine-local and gitignored; carry them
# across so a published build keeps the user's video and path settings.
foreach ($name in @('settings.json', 'interface.ini')) {
    $file = Join-Path $root $name
    if (Test-Path -LiteralPath $file) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $OutputDirectory $name) -Force
        $copied.Add($name)
    }
}

if (-not $SingleFile) {
    # A RID-specific self-contained publish flattens the native libraries beside
    # the exe rather than leaving them under runtimes/. Both layouts are accepted
    # because that is a packaging detail, but their presence is not: missing
    # cimgui.dll or SDL2.dll means the window or the pads fail at runtime.
    $nativeNames = @('cimgui.dll', 'glfw3.dll', 'SDL2.dll', 'soft_oal.dll')
    $required = @(
        Join-Path $OutputDirectory 'RE15pc.dll'
        Join-Path $OutputDirectory 'RecompOne.Runtime.dll'
    )
    foreach ($native in $nativeNames) {
        $flat = Join-Path $OutputDirectory $native
        $nested = Join-Path $OutputDirectory "runtimes/$RuntimeIdentifier/native/$native"
        if (Test-Path -LiteralPath $flat) { $required += $flat }
        elseif (Test-Path -LiteralPath $nested) { $required += $nested }
        else { throw "publish is missing $native; the window or controller support will fail at runtime" }
    }
    foreach ($item in $required) {
        if (-not (Test-Path -LiteralPath $item)) {
            throw "publish is missing $([IO.Path]::GetFileName($item))"
        }
    }
}

# Prove the shell identity landed rather than assuming it did. ApplicationIcon is
# silently ignored when the .ico is unreadable, and a missing icon is easy to miss
# until the exe has already shipped.
Add-Type -AssemblyName System.Drawing
$shellIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if (-not $shellIcon) { throw "no icon resource found in $exe" }
try { $iconSize = "$($shellIcon.Width)x$($shellIcon.Height)" }
finally { $shellIcon.Dispose() }

$exeItem = Get-Item -LiteralPath $exe
$version = $exeItem.VersionInfo
$allFiles = Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse
$totalBytes = ($allFiles | Measure-Object -Property Length -Sum).Sum

Write-Host ''
Write-Host ('published {0} for {1} - {2} files, {3:N1} MB total' -f `
    $exeItem.Name, $RuntimeIdentifier, $allFiles.Count, ($totalBytes / 1MB))
Write-Host ("  exe            {0:N1} MB" -f ($exeItem.Length / 1MB))
Write-Host ("  packaging      {0}" -f $(if ($SingleFile) { 'single file (known to crash at shutdown)' } else { 'self-contained folder' }))
Write-Host ("  product        {0} {1}" -f $version.ProductName, $version.ProductVersion)
Write-Host ("  shell icon     {0}" -f $iconSize)
Write-Host ("  disc alongside {0}" -f $(if ($copied.Count -gt 0) { @($copied) -join ', ' } else { '(none - supply your own Bio2Nov96.bin/cue)' }))
Write-Host ''
Write-Host ("run it with: {0}" -f (Join-Path $OutputDirectory 'RE15pc.exe'))
