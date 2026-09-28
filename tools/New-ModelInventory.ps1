[CmdletBinding()]
param([string] $Output, [switch] $Check)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $RepoRoot 'docs/model-inventory.json' }
$Manifest = Get-Content (Join-Path $RepoRoot 'disc-manifest.json') -Raw | ConvertFrom-Json
$ImagePath = Join-Path $RepoRoot $Manifest.disc.file
if ((Get-FileHash $ImagePath -Algorithm SHA256).Hash -ne $Manifest.disc.sha256) { throw 'wrong disc' }
$Disc = [IO.File]::OpenRead($ImagePath)
try {
    $Files = foreach ($Entry in $Manifest.files | Where-Object { $_.path -match '\.(EMS|PLD|PLW)$' } | Sort-Object path) {
        $Bytes = [byte[]]::new($Entry.size)
        for ($Done = 0; $Done -lt $Bytes.Length; $Done += 2048) {
            $Take = [math]::Min(2048, $Bytes.Length - $Done)
            $Lba = [long]$Entry.lba + [long][math]::Floor($Done / 2048)
            $null = $Disc.Seek($Lba * 2352 + 24, [IO.SeekOrigin]::Begin)
            if ($Disc.Read($Bytes, $Done, $Take) -ne $Take) { throw 'short model read' }
        }
        $Blocks = [Collections.Generic.List[object]]::new()
        $Offset = 0
        $Issue = ''
        while ($Offset -lt $Bytes.Length) {
            if ($Offset + 8 -gt $Bytes.Length) { $Issue = 'truncated header'; break }
            $Directory = [BitConverter]::ToUInt32($Bytes, $Offset)
            $Count = [BitConverter]::ToUInt32($Bytes, $Offset + 4)
            $End = [long]$Offset + $Directory + [long]$Count * 4
            if ($Count -lt 1 -or $Count -gt 32 -or $Directory -lt 8 -or $End -gt $Bytes.Length) {
                $Issue = 'unrecognized relative section directory'; break
            }
            $Sections = @()
            for ($i = 0; $i -lt $Count; $i++) {
                $Value = [BitConverter]::ToUInt32($Bytes, $Offset + $Directory + $i * 4)
                if ($Value -lt 8 -or $Value -ge $Directory) { $Issue = 'section offset outside payload'; break }
                $Sections += $Value
            }
            if ($Issue) { break }
            $Blocks.Add([ordered]@{ index=$Blocks.Count; offset=$Offset; length=($End-$Offset);
                directoryOffset=$Directory; sectionOffsets=$Sections })
            if ($End -eq $Bytes.Length) { $Offset = $Bytes.Length; break }
            if (-not $Entry.path.EndsWith('.EMS')) { $Offset = $End; $Issue = 'unaccounted trailing data'; break }
            $Next = [long][math]::Ceiling($End / 2048) * 2048
            if ($Next -gt $Bytes.Length) { $Offset = $End; $Issue = 'truncated sector padding'; break }
            $Offset = $Next
        }
        [ordered]@{ path=$Entry.path; size=$Entry.size; structurallyAccounted=($Offset -eq $Bytes.Length -and -not $Issue);
            stopOffset=$Offset; issue=$Issue; blocks=@($Blocks) }
    }
}
finally { $Disc.Dispose() }
$Result = [ordered]@{ schemaVersion=1; discSha256=$Manifest.disc.sha256;
    scope='Relative section directories and sector-aligned model blocks only. Semantic identities, animations, rendering and gameplay remain unverified.';
    files=@($Files) }
$Json = ($Result | ConvertTo-Json -Depth 10) + [Environment]::NewLine
if ($Check) {
    $Existing = Get-Content -LiteralPath $Output -Raw
    if ($Existing.Replace("`r`n","`n") -ne $Json.Replace("`r`n","`n")) { throw 'model inventory differs from disc' }
}
else { [IO.File]::WriteAllText($Output, $Json) }
$Parsed = @($Files | Where-Object structurallyAccounted).Count
$BlockCount = ($Files | ForEach-Object { $_.blocks.Count } | Measure-Object -Sum).Sum
Write-Host "MODEL STRUCTURE: $Parsed/$($Files.Count) files accounted for; $BlockCount blocks"
foreach ($File in $Files | Where-Object { -not $_.structurallyAccounted }) {
    Write-Host "UNRESOLVED $($File.path): $($File.issue) at $($File.stopOffset)"
}
Write-Host 'Structural accounting is not a model/animation playability gate.'
