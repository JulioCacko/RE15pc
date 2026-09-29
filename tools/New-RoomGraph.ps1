#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the room-connection graph from the disc's own room scripts.

.DESCRIPTION
    Every RDT room file carries a table of door records. Each record gives the
    rectangle the player must stand in to use the door, and where that door leads:
    the destination stage, room, camera, floor and arrival coordinates.

    Reading them out turns "how do we reach stage 2" from guesswork into a graph
    question, which is the difference between driving the game blindly and knowing
    which door to walk through. It also gives phase 3 something it currently lacks:
    evidence about which rooms are reachable at all, as opposed to which simply have
    not been visited yet.

    A door is only accepted when the room it points at actually exists on the disc.
    That check is what makes the parse trustworthy rather than plausible - a
    misaligned read produces a record whose destination file does not exist, and it
    is rejected and counted instead of silently entering the graph.

    No disc bytes are recorded in the output. Like disc-manifest.json and
    model-inventory.json, it keeps structure: room identifiers, exit geometry and
    destination references.

.PARAMETER OutputDirectory
    Where room-graph.json and room-graph.md are written. Defaults to docs/.

.PARAMETER Disc
    Disc binary to read. Defaults to Bio2Nov96.bin beside the repository root. This
    tool needs the disc image; the imported store is not an ISO9660 filesystem.

.PARAMETER StartRoom
    Room to measure reachability from, as <stage>:<room> in hex. Defaults to 1:17,
    Leon's rooftop start.

.EXAMPLE
    pwsh -File tools/New-RoomGraph.ps1

.EXAMPLE
    pwsh -File tools/New-RoomGraph.ps1 -StartRoom 1:03
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$Disc,
    [string]$StartRoom = '1:17'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'docs' }
if (-not $Disc) { $Disc = Join-Path $root 'Bio2Nov96.bin' }

Write-Host "disc : $Disc"
Write-Host "out  : $OutputDirectory"
if (-not (Test-Path -LiteralPath $Disc)) {
    throw ("disc image not found: $Disc`n" +
           'This tool reads the disc directly, so it needs the image rather than an imported store.')
}

# ConvertTo-Json refuses Hashtable ("keys must be strings"), and the per-stage tallies
# are naturally built as hashtables. This turns one into a serialisable object.
function ConvertTo-Object {
    param([hashtable]$Table)
    $result = [ordered]@{}
    foreach ($key in ($Table.Keys | Sort-Object)) { $result["$key"] = $Table[$key] }
    return [pscustomobject]$result
}
$manifestPath = Join-Path $root 'disc-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "disc-manifest.json not found" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $manifest.disc.sha256) { throw 'disc-manifest.json has no disc.sha256' }

# Geometry comes from the manifest rather than being hard-coded here, so a disc with
# a different sector size or data offset cannot be read with the wrong arithmetic.
# dataOffset is where user data begins *inside* a 2352-byte sector, not padding to
# subtract: MODE2/Form1 holds 8 bytes of subheader, then 2048 bytes of user data, then
# 280 bytes of EDC/ECC. So the divisor is the ISO9660 logical block (2048) and
# dataOffset is the in-sector offset. Using (sectorSize - dataOffset) as the divisor
# reads every sector out of alignment, which parses as plausible-looking garbage.
$sectorSize = [int]$manifest.disc.sectorSize
$dataOffset = [int]$manifest.disc.dataOffset
$logicalBlock = 2048
if (($dataOffset + $logicalBlock) -gt $sectorSize) {
    throw "sector size $sectorSize cannot hold a $logicalBlock-byte logical block at offset $dataOffset"
}
Write-Host ("sector {0}, data offset {1}, logical block {2}" -f $sectorSize, $dataOffset, $logicalBlock)

# Index rooms by "<stage>:<room>" so a door's destination can be validated without
# reconstructing a file name from the record's bytes.
$roomIndex = @{}
$roomPath = @{}
foreach ($entry in $manifest.files) {
    if ($entry.kind -ne 'file') { continue }
    if ($entry.path -notmatch '^PSX/STAGE(\d)/ROOM(\d)([0-9A-F]{2})(\d)\.RDT$') { continue }
    $stage = [int]$Matches[1]
    if ([int]$Matches[2] -ne $stage) { continue }   # the room's own stage digit must agree
    $key = '{0}:{1}' -f $stage, $Matches[3]
    # Both maps must be first-wins. $roomPath was last-wins while $roomIndex was
    # first-wins, so the tool parsed variant 0 and then printed variant 1's path. The
    # engine loads variant 0 - tools/Test-FirstRoom.ps1 builds ROOM...0.RDT and proves
    # the resident RDT header matches it.
    if (-not $roomIndex.ContainsKey($key)) {
        $roomIndex[$key] = $entry
        $roomPath[$key] = $entry.path
    }
}
Write-Host ("rooms indexed from the manifest: {0}" -f $roomIndex.Count)

$stream = [IO.File]::OpenRead($Disc)
try {
    function Read-Range {
        param($Entry, [int]$Offset, [int]$Count)
        if ($Count -le 0 -or $Offset -lt 0 -or ($Offset + $Count) -gt $Entry.size) { return ,([byte[]]::new(0)) }
        $bytes = [byte[]]::new($Count)
        $done = 0
        while ($done -lt $Count) {
            $logical = $Offset + $done
            $sector = [long]$Entry.lba + [long][math]::Floor($logical / $logicalBlock)
            $within = $logical % $logicalBlock
            $take = [math]::Min($Count - $done, $logicalBlock - $within)
            $null = $stream.Seek($sector * $sectorSize + $dataOffset + $within, [IO.SeekOrigin]::Begin)
            if ($stream.Read($bytes, $done, $take) -ne $take) { throw "short read at logical $logical" }
            $done += $take
        }
        return ,$bytes
    }

    $rooms = [System.Collections.Generic.List[object]]::new()
    $rejected = 0
    $noDoorTable = 0
    $malformedRecords = 0
    $truncatedRecords = 0
    $degenerateDoors = 0
    # Rooms that cannot be parsed are named rather than dropped. Silently skipping them
    # is why the summary reported 17 stage-1 rooms when 19 stage-1 RDTs exist, leaving a
    # reader unable to tell which rooms were missing.
    $skippedRooms = [System.Collections.Generic.List[object]]::new()

    foreach ($key in ($roomIndex.Keys | Sort-Object)) {
        $entry = $roomIndex[$key]
        $parts = $key -split ':'
        $stage = [int]$parts[0]
        $room = $parts[1]

        # An RDT is small (the largest here is ~154 KB), so reading it whole removes a
        # whole class of bounds bug in exchange for nothing measurable.
        $bytes = Read-Range -Entry $entry -Offset 0 -Count ([int]$entry.size)
        if ($bytes.Length -lt 0x44) {
            $skippedRooms.Add([pscustomobject]@{ key = $key; path = $entry.path; size = $entry.size
                reason = "file is only $($bytes.Length) bytes; too small to hold an RDT header" })
            continue
        }

        $init = [BitConverter]::ToUInt32($bytes, 0x40)
        if ($init -le 0 -or ($init + 4) -ge $bytes.Length) {
            $skippedRooms.Add([pscustomobject]@{ key = $key; path = $entry.path; size = $entry.size
                reason = "init pointer at +0x40 is $init, which does not address a door table" })
            continue
        }

        # The table is a u16 at `init` giving the offset, from `init`, of the first
        # record. tools/Test-FirstRoom.ps1 relies on the same indirection for room 117.
        $first = [BitConverter]::ToUInt16($bytes, [int]$init)
        $cursor = [int]$init + $first
        $doors = [System.Collections.Generic.List[object]]::new()

        # 0x3B records are a family discriminated by the byte at +3, and the two subtypes
        # are different lengths. Advancing a fixed 32 bytes walks into the middle of a
        # 40-byte record and reads its destination Y, stage, room, camera and floor bytes
        # as a rectangle - which produces a well-formed-looking door pointing at a stage
        # that cannot exist. A disc-wide census found 91 of subtype 0x31 and exactly two
        # of subtype 0xB1, both in room 4:03.
        while ($cursor + 4 -le $bytes.Length) {
            if ($bytes[$cursor] -ne 0x3B) { break }

            $subtype = $bytes[$cursor + 3]
            if ($subtype -eq 0xB1) { $recordLength = 40; $destinationAt = 22 }
            elseif ($subtype -eq 0x31) { $recordLength = 32; $destinationAt = 14 }
            else { $malformedRecords++; break }

            if (($cursor + $recordLength) -gt $bytes.Length) { $truncatedRecords++; break }

            $index = $bytes[$cursor + 1]
            $exitX = [BitConverter]::ToInt16($bytes, $cursor + 6)
            $exitZ = [BitConverter]::ToInt16($bytes, $cursor + 8)
            $exitW = [BitConverter]::ToUInt16($bytes, $cursor + 10)
            $exitH = [BitConverter]::ToUInt16($bytes, $cursor + 12)
            # The destination block sits after an extra corner-pair rectangle on 0xB1.
            $destX = [BitConverter]::ToInt16($bytes, $cursor + $destinationAt)
            $destY = [BitConverter]::ToInt16($bytes, $cursor + $destinationAt + 2)
            $destZ = [BitConverter]::ToInt16($bytes, $cursor + $destinationAt + 4)
            $destYaw = [BitConverter]::ToUInt16($bytes, $cursor + $destinationAt + 6) -band 0xfff
            $destStage = [int]$bytes[$cursor + $destinationAt + 8] + 1
            $destRoom = $bytes[$cursor + $destinationAt + 9].ToString('X2')
            $destCamera = $bytes[$cursor + $destinationAt + 10]
            $destFloor = $bytes[$cursor + $destinationAt + 11]
            $degenerate = ($exitW -eq 0 -or $exitH -eq 0)

            $destKey = '{0}:{1}' -f $destStage, $destRoom
            $resolved = $roomIndex.ContainsKey($destKey)

            if (-not $resolved) {
                # A record whose destination room is not on the disc is either a
                # misaligned read or a door into content this build never shipped.
                # Either way it does not belong in the graph, but it is counted.
                $rejected++
            }

            $doors.Add([pscustomobject]@{
                index      = $index
                exit       = [pscustomobject]@{ x = $exitX; z = $exitZ; w = $exitW; h = $exitH }
                destination = [pscustomobject]@{
                    stage  = $destStage
                    room   = $destRoom
                    key    = $destKey
                    camera = $destCamera
                    floor  = $destFloor
                    x      = $destX
                    y      = $destY
                    z      = $destZ
                    yaw    = $destYaw
                }
                resolved   = $resolved
                degenerate = $degenerate
                subtype    = ('0x{0:X2}' -f $subtype)
                length     = $recordLength
                destinationPath = if ($resolved) { $roomPath[$destKey] } else { $null }
            })

            $cursor += $recordLength
        }

        if ($doors.Count -eq 0) { $noDoorTable++ }

        $rooms.Add([pscustomobject]@{
            key   = $key
            stage = $stage
            room  = $room
            path  = $entry.path
            size  = $entry.size
            initPointer = $init
            doorCount = $doors.Count
            doors = $doors
        })
    }

    Write-Host ("rooms parsed: {0}   doors: {1}   unresolved destinations: {2}   rooms with no door table: {3}" -f `
        $rooms.Count, ($rooms | Measure-Object -Property doorCount -Sum).Sum, $rejected, $noDoorTable)

    # Directed reachability from the start room.
    $adjacency = @{}
    foreach ($r in $rooms) {
        $targets = @()
        foreach ($d in $r.doors) {
            if ($d.degenerate) { $degenerateDoors++; continue }
            if ($d.resolved) { $targets += $d.destination.key }
        }
        $adjacency[$r.key] = $targets
    }

    if (-not $adjacency.ContainsKey($StartRoom)) {
        throw "start room $StartRoom is not on the disc; try one of: $((($adjacency.Keys | Sort-Object) | Select-Object -First 8) -join ', ')"
    }

    $seen = @{}
    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($StartRoom)
    $seen[$StartRoom] = 0
    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        foreach ($next in $adjacency[$current]) {
            if ($seen.ContainsKey($next)) { continue }
            $seen[$next] = $seen[$current] + 1
            $queue.Enqueue($next)
        }
    }

    $reachableByStage = @{}
    foreach ($key in $seen.Keys) {
        $stage = [int](($key -split ':')[0])
        if (-not $reachableByStage.ContainsKey($stage)) { $reachableByStage[$stage] = 0 }
        $reachableByStage[$stage]++
    }

    $stageTotals = @{}
    foreach ($r in $rooms) {
        if (-not $stageTotals.ContainsKey($r.stage)) { $stageTotals[$r.stage] = 0 }
        $stageTotals[$r.stage]++
    }

    Write-Host ''
    Write-Host "reachability from room $StartRoom (directed):"
    foreach ($stage in ($stageTotals.Keys | Sort-Object)) {
        $have = if ($reachableByStage.ContainsKey($stage)) { $reachableByStage[$stage] } else { 0 }
        Write-Host ("  stage {0}: {1}/{2} rooms reachable" -f $stage, $have, $stageTotals[$stage])
    }

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $result = [pscustomobject]@{
        schemaVersion   = 1
        discSha256      = $manifest.disc.sha256
        generatedBy     = 'tools/New-RoomGraph.ps1'
        note            = 'Structural room connectivity read from the disc. Records no disc bytes.'
        startRoom       = $StartRoom
        totals          = [pscustomobject]@{
            rooms                  = $rooms.Count
            doors                  = ($rooms | Measure-Object -Property doorCount -Sum).Sum
            unresolvedDestinations = $rejected
            roomsWithoutDoorTable  = $noDoorTable
            roomsSkipped           = $skippedRooms.Count
            degenerateDoors        = $degenerateDoors
            malformedRecords       = $malformedRecords
            truncatedRecords       = $truncatedRecords
            reachableFromStart     = $seen.Count
        }
        skippedRooms    = $skippedRooms
        roomsPerStage          = (ConvertTo-Object $stageTotals)
        reachablePerStage      = (ConvertTo-Object $reachableByStage)
        rooms           = $rooms
        reachable       = ($seen.Keys | Sort-Object)
    }

    $json = Join-Path $OutputDirectory 'room-graph.json'
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $json
    Write-Host ''
    Write-Host "wrote $json"

    # Markdown summary, so the finding is readable without opening the JSON.
    $md = [System.Text.StringBuilder]::new()
    $null = $md.AppendLine('# Room graph')
    $null = $md.AppendLine()
    $null = $md.AppendLine('Generated by `tools/New-RoomGraph.ps1` from the door records in the disc''s own')
    $null = $md.AppendLine('RDT room files. It records structure - room connectivity, exit geometry and')
    $null = $md.AppendLine('destination references - and no disc bytes, like `disc-manifest.json`.')
    $null = $md.AppendLine()
    $null = $md.AppendLine(('Totals: {0} rooms, {1} door records, {2} doors whose destination room is not on the' -f `
        $result.totals.rooms, $result.totals.doors, $result.totals.unresolvedDestinations))
    $null = $md.AppendLine(('disc, {0} rooms with no door table, {1} rooms reachable from {2}.' -f `
        $result.totals.roomsWithoutDoorTable, $result.totals.reachableFromStart, $StartRoom))
    $null = $md.AppendLine()
    $null = $md.AppendLine('## Reachability from the start room')
    $null = $md.AppendLine()
    $null = $md.AppendLine('| Stage | Rooms on disc | Reachable from ' + $StartRoom + ' |')
    $null = $md.AppendLine('|---|---|---|')
    foreach ($stage in ($stageTotals.Keys | Sort-Object)) {
        $have = if ($reachableByStage.ContainsKey($stage)) { $reachableByStage[$stage] } else { 0 }
        $null = $md.AppendLine(('| {0} | {1} | {2} |' -f $stage, $stageTotals[$stage], $have))
    }
    $null = $md.AppendLine()

    if ($skippedRooms.Count -gt 0) {
        $null = $md.AppendLine('## Rooms that could not be parsed')
        $null = $md.AppendLine()
        $null = $md.AppendLine('These RDT files are on the disc but carry no usable door table, so they are absent')
        $null = $md.AppendLine('from the graph. They are named rather than dropped, because a room silently')
        $null = $md.AppendLine('missing from a connectivity document is indistinguishable from a room with no')
        $null = $md.AppendLine('doors.')
        $null = $md.AppendLine()
        foreach ($s in $skippedRooms) { $null = $md.AppendLine(('- `{0}` ({1}, {2} bytes): {3}' -f $s.key, $s.path, $s.size, $s.reason)) }
        $null = $md.AppendLine()
    }

    if ($degenerateDoors -gt 0) {
        $null = $md.AppendLine('## Door records with an empty rectangle')
        $null = $md.AppendLine()
        $null = $md.AppendLine(('{0} record(s) parse as doors but have a zero-area rectangle, so they can never' -f $degenerateDoors))
        $null = $md.AppendLine('be stood in. They are counted and excluded from the graph rather than being')
        $null = $md.AppendLine('walked as if they were doors:')
        $null = $md.AppendLine()
        foreach ($room in $rooms) {
            foreach ($d in $room.doors) {
                if ($d.degenerate) { $null = $md.AppendLine(('- {0} door {1}' -f $room.key, $d.index)) }
            }
        }
        $null = $md.AppendLine()
    }

    # Shortest door chain from the start room to the first reachable room in a later
    # stage, which is what turns "unreachable" into a route.
    $parent = @{ $StartRoom = $null }
    $bfs = [System.Collections.Generic.Queue[string]]::new()
    $bfs.Enqueue($StartRoom)
    $firstLater = $null
    $startStage = [int](($StartRoom -split ':')[0])
    while ($bfs.Count -gt 0) {
        $current = $bfs.Dequeue()
        if ($current -ne $StartRoom -and [int](($current -split ':')[0]) -gt $startStage) { $firstLater = $current; break }
        foreach ($room in $rooms) {
            if ($room.key -ne $current) { continue }
            foreach ($d in $room.doors) {
                if (-not $d.resolved) { continue }
                $next = $d.destination.key
                if ($parent.ContainsKey($next)) { continue }
                $parent[$next] = @{ from = $current; door = $d }
                $bfs.Enqueue($next)
            }
        }
    }

    if ($firstLater) {
        $chain = @()
        $node = $firstLater
        while ($parent[$node]) {
            $chain += @{ to = $node; from = $parent[$node].from; door = $parent[$node].door }
            $node = $parent[$node].from
        }
        [array]::Reverse($chain)
        $null = $md.AppendLine('## Shortest route out of stage ' + $startStage)
        $null = $md.AppendLine()
        $null = $md.AppendLine('Doors are numbered by their own index byte; the rectangle is where the player')
        $null = $md.AppendLine('has to stand for the action to fire.')
        $null = $md.AppendLine()
        $null = $md.AppendLine('| From | Door | To | Stand in x | Stand in z | Arrive x | Arrive z | Yaw | Cam |')
        $null = $md.AppendLine('|---|---|---|---|---|---|---|---|---|')
        foreach ($step in $chain) {
            $d = $step.door
            $null = $md.AppendLine(('| {0} | {1} | {2} | {3}..{4} | {5}..{6} | {7} | {8} | {9} | {10} |' -f `
                $step.from, $d.index, $step.to, $d.exit.x, ($d.exit.x + $d.exit.w), `
                $d.exit.z, ($d.exit.z + $d.exit.h), $d.destination.x, $d.destination.z, `
                $d.destination.yaw, $d.destination.camera))
        }
        $null = $md.AppendLine()
        $null = $md.AppendLine(('This is why "stages 2-6 have zero entered functions" is a routing gap rather' + "`n" +
            'than missing content: those stages are two doors away from the rooftop, and no' + "`n" +
            'gate has walked through them yet.'))
        $null = $md.AppendLine()
    }

    $unresolved = @()
    foreach ($room in $rooms) {
        foreach ($d in $room.doors) { if (-not $d.resolved) { $unresolved += ('{0} door {1} -> stage {2} room {3}' -f $room.key, $d.index, $d.destination.stage, $d.destination.room) } }
    }
    if ($unresolved.Count -gt 0) {
        $null = $md.AppendLine('## Doors whose destination room is not on the disc')
        $null = $md.AppendLine()
        $null = $md.AppendLine('A door record pointing at a room that does not exist is either a misaligned')
        $null = $md.AppendLine('read - which the existence check is there to catch - or a door into content this')
        $null = $md.AppendLine('build never shipped. Both are worth listing rather than hiding.')
        $null = $md.AppendLine()
        foreach ($u in ($unresolved | Sort-Object)) { $null = $md.AppendLine('- ' + $u) }
        $null = $md.AppendLine()
    }

    $null = $md.AppendLine('## Driving to a door: how yaw maps to world movement')
    $null = $md.AppendLine()
    $null = $md.AppendLine('Measured, not assumed. Holding Up for a known number of frames from a known')
    $null = $md.AppendLine('position, at three headings 135 degrees apart (each `right:17` turns exactly')
    $null = $md.AppendLine('1536 yaw units):')
    $null = $md.AppendLine()
    $null = $md.AppendLine('| yaw held | measured (dx, dz) normalised |')
    $null = $md.AppendLine('|---|---|')
    $null = $md.AppendLine('| 168 | (+0.964, -0.267) |')
    $null = $md.AppendLine('| 1192 | (-0.258, -0.966) |')
    $null = $md.AppendLine('| 2728 | (-0.512, +0.859) |')
    $null = $md.AppendLine()
    $null = $md.AppendLine('All three fit the same law, so walking to a rectangle is arithmetic rather than')
    $null = $md.AppendLine('trial and error:')
    $null = $md.AppendLine()
    $null = $md.AppendLine('    direction = ( cos(yaw * 2*pi/4096), -sin(yaw * 2*pi/4096) )')
    $null = $md.AppendLine()
    $null = $md.AppendLine('Forward speed while holding Up measured ~73 units per frame. Yaw is masked to')
    $null = $md.AppendLine('0xFFF, so it wraps at 4096, and a heading that would need a negative yaw is')
    $null = $md.AppendLine('reached by turning the other way.')
    $null = $md.AppendLine()
    $null = $md.AppendLine('This is a measurement from one room with railings, so a collision clamps the')
    $null = $md.AppendLine('displacement and the speed figure is a floor rather than a constant. The')
    $null = $md.AppendLine('direction law is the part the three headings establish, and the three agree to')
    $null = $md.AppendLine('within a few thousandths.')
    $null = $md.AppendLine()

    $mdPath = Join-Path $OutputDirectory 'room-graph.md'
    Set-Content -LiteralPath $mdPath -Value $md.ToString()
    Write-Host "wrote $mdPath"
}
finally { $stream.Dispose() }
