<#
.SYNOPSIS
    Restores the RE15pc build environment.

.DESCRIPTION
    RE15pc does not vendor RecompOne. This script reproduces the exact toolchain
    the port was developed against:

      1. activates the tracked Conventional Commits hook
      2. clones BlackLabelHQ/RecompOne at a pinned commit
      3. applies every patch in patches/ in filename order
      4. restores and builds the solution

    It is idempotent. Re-running with a clean tree is a no-op beyond the build.

.PARAMETER Force
    Discard the existing RecompOne working clone and re-clone from scratch.
    Use this when a patch fails to apply and you want a known-clean base.

.PARAMETER SkipBuild
    Stop after the working clone is prepared and patched.

.EXAMPLE
    pwsh -File bootstrap.ps1

.EXAMPLE
    pwsh -File bootstrap.ps1 -Force
#>
[CmdletBinding()]
param(
    # Upstream commit the port was developed and verified against.
    # See docs/compatibility.md before changing this.
    [string] $RecompOneCommit = 'd81dec8c9622fdcd0865d73588a3baa8d3c3a605',

    [string] $RecompOneRemote = 'https://github.com/BlackLabelHQ/RecompOne.git',

    [switch] $Force,
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$CloneDir = Join-Path $RepoRoot 'RecompOne'
$PatchDir = Join-Path $RepoRoot 'patches'
$Solution = Join-Path $RepoRoot 'RE15PC.sln'

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Test-Command {
    param([string] $Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' was not found on PATH. Install it and re-run bootstrap.ps1."
    }
}

Write-Step 'Checking prerequisites'
Test-Command 'git'
Test-Command 'dotnet'
Write-Host "    git:    $((git --version) -replace '^git version ', '')"
Write-Host "    dotnet: $(dotnet --version)"

# ---------------------------------------------------------------------------
# 1. Conventional Commits hook
# ---------------------------------------------------------------------------
Write-Step 'Activating the commit-msg hook'
Push-Location $RepoRoot
try {
    git config core.hooksPath .githooks
    $configured = (git config core.hooksPath).Trim()
    if ($configured -ne '.githooks') {
        throw "expected core.hooksPath to be '.githooks' but got '$configured'."
    }
    Write-Host "    core.hooksPath = $configured"
}
finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# 2. RecompOne working clone at the pinned commit
# ---------------------------------------------------------------------------
if ($Force -and (Test-Path -LiteralPath $CloneDir)) {
    Write-Step 'Removing the existing RecompOne clone (-Force)'
    Remove-Item -LiteralPath $CloneDir -Recurse -Force
}

if (-not (Test-Path -LiteralPath $CloneDir)) {
    Write-Step "Cloning RecompOne into $CloneDir"
    git clone --quiet $RecompOneRemote $CloneDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed ($LASTEXITCODE)." }
}
else {
    Write-Step 'Reusing the existing RecompOne clone'
}

Write-Step "Checking out pinned commit $RecompOneCommit"
Push-Location $CloneDir
try {
    git fetch --quiet origin
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed ($LASTEXITCODE)." }

    git checkout --quiet $RecompOneCommit
    if ($LASTEXITCODE -ne 0) {
        throw "could not check out '$RecompOneCommit'. It may no longer exist upstream; see docs/compatibility.md."
    }

    $head = (git rev-parse HEAD).Trim()
    if ($head -ne $RecompOneCommit) {
        throw "expected HEAD $RecompOneCommit but got $head."
    }
    Write-Host "    HEAD = $head"

    # Always start from a clean base so patches apply deterministically.
    git checkout --quiet -- .
    git clean --quiet -fd
}
finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# 3. patches/
# ---------------------------------------------------------------------------
Write-Step 'Applying patches'
$patches = @()
if (Test-Path -LiteralPath $PatchDir) {
    $patches = Get-ChildItem -LiteralPath $PatchDir -Filter '*.patch' -File |
        Sort-Object Name
}

if ($patches.Count -eq 0) {
    Write-Host '    no patches to apply'
}
else {
    Push-Location $CloneDir
    try {
        foreach ($patch in $patches) {
            Write-Host "    applying $($patch.Name)"
            git apply --3way --whitespace=nowarn $patch.FullName
            if ($LASTEXITCODE -ne 0) {
                throw @"
patch '$($patch.Name)' did not apply cleanly.

Upstream RecompOne has probably moved since $RecompOneCommit, or the patch
needs rebasing. Recover with:

    pwsh -File bootstrap.ps1 -Force

then regenerate the patch from a clean clone and update docs/compatibility.md.
"@
            }
        }
    }
    finally {
        Pop-Location
    }
    Write-Host "    applied $($patches.Count) patch(es)"
}

# ---------------------------------------------------------------------------
# 4. Build
# ---------------------------------------------------------------------------
if ($SkipBuild) {
    Write-Step 'Skipping build (-SkipBuild)'
    Write-Host 'bootstrap.ps1 finished.'
    exit 0
}

if (-not (Test-Path -LiteralPath $Solution)) {
    Write-Step 'Solution not present yet'
    Write-Host "    $Solution does not exist; skipping build."
    Write-Host 'bootstrap.ps1 finished.'
    exit 0
}

Write-Step "Restoring and building $([IO.Path]::GetFileName($Solution))"
dotnet build $Solution -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)." }

Write-Step 'bootstrap.ps1 finished'
