# Full-disc coverage

The disc manifest accounts for 334 files, including 96 RDT room-data files,
61 background containers, nine character models, 43 weapon models and two
packed enemy-model containers. This inventory is not a playability verdict.

## Capture and inventory

```powershell
dotnet build RecompOne/RecompOne.Recompiler/RecompOne.Recompiler.csproj -c Release
dotnet RecompOne/RecompOne.Recompiler/bin/Release/net10.0/recompone.dll port/config/bio2nov96.json
dotnet build tests/RE15pc.Checks/RE15pc.Checks.csproj -c Release
pwsh -File tools/Test-FirstRoom.ps1 -OutputRoot out/gates/leon-coverage
pwsh -File tools/Test-ElzaEntry.ps1 -OutputRoot out/gates/elza-coverage
& ./tools/Update-ContentCoverage.ps1 -RunDir @('out/gates/leon-coverage/route', 'out/gates/elza-coverage/route')
pwsh -File tools/Test-ContentInventory.ps1
```

When invoking from a native shell rather than PowerShell, pass the two run
directories through a PowerShell array expression. Existing output directories
must not be reused.

`--trace-coverage` records generated function entry points and actual sector
accesses through DiscFs. This includes direct generated calls and SDK replacement
wrappers; observing Dispatcher alone would miss direct calls. The observer is
disabled by default and never changes guest registers or disc data.

`coverage.json` separates entered/unentered mapped functions by overlay and
touched/complete file extents by LBA. Repeated partial reads do not add up to
a fictitious full-sector read. Tracing is disabled after the guest stops and
before synthetic overlay verification.

The ledger in `content-coverage.json` preserves separate run revisions,
configuration hashes and input routes. Importing observations can promote a file
only from unverified to partial. It cannot mark a file verified.

## Limits that still block full-content acceptance

- A function entry hit does not establish conditional-branch or path coverage.
- A complete disc read does not prove decoding, presentation or interaction.
- Character selection loads preview models for both characters. Those reads do
  not prove both are playable.
- Packed animations, models, items, samples, sequences, background images and
  room-script branches need member-level accounting.
- Mapped functions may include unused library routines or heuristic candidates.
  Unentered is not synonymous with unreachable; each exclusion needs evidence.
- Runs from different configurations must not be combined into a release verdict.
- Stages 2–6 still need native execution and gameplay evidence. `docs/room-graph.md`,
  built from the disc's own door records by `tools/New-RoomGraph.ps1`, shows stage 2
  two doors from the rooftop start and stages 3–5 behind it. That is a graph
  result about connectivity, not a proven walkable route, and one measurement now
  argues against the first step of it: driving the rooftop with scripted input pins
  the player at z = -6000 against a collision boundary, while room 117's door 1 -
  the only door in the graph leading to room 10B, and therefore the only entry to
  stage 2 on this path - has an exit rectangle at z = -9400..-6480, beyond that
  boundary. Either the room has a second level reachable some other way, or that
  door is not walkable and the graph's reachability of stages 2-5 is optimistic.
  Settling which is the next step, and it is exactly the kind of claim the graph
  tool exists to make checkable.

Structural inspection found 25 sector-aligned model blocks in each CDEMD
container. Their relative section directories and padded extents account for
each complete file. Semantic identities, animation coverage and member-level
playability have not yet been certified.

## Entry-path fixes exposed by coverage

Elza's entry originally stopped guest thread 1 on `0x80100424`, while the
main VSync thread continued. Older reporting incorrectly called that a pass.
Patch 0011 preserves the first guest failure, cancels execution, and wakes
callers through the cooperative thread boundary. Intentional stop signals
are handled separately. The recorded negative run now fails at frame 1537
with one unmapped call.

The missing STAGE1 callback is real code: the 612 bytes from 0x80100424 through
0x80100687 match disc and live RAM, start with a stack prologue and end with a
return. The curated map now exposes this entry. The prior entry starting at
0x801000A4 had absorbed the callback after pointer-table data.

That repair exposed four missing destinations in the background decoder's
computed-copy table: 0x8001031C, 0x80010334, 0x80010340 and 0x80010490.
The original code computes `0x80010310 + 12 * skip`. All 33 suffixes are now
dispatchable. The regression executes the original generated routine for
lengths 0–65, the observed failing length 31, and a 1024-byte multi-block copy,
checking data, bounds and pointer advancement.

The old `out/runs/elza-entry-coverage` report is invalid despite its historical
PASS text. The importer rejects legacy failure tracking and fault-bearing logs.
Do not use that artifact as accepted coverage.

## Rooms the disc names but never shipped a script for

Six door records point at room identifiers that have a background container on the
disc but no RDT room script:

| Door | Points at | Background container present |
|---|---|---|
| `1:21` door 2 | room `1:23` | `PSX/STAGE1/ROOM123.BSS` |
| `1:21` door 3 | room `1:24` | `PSX/STAGE1/ROOM124.BSS` |
| `1:21` door 4 | room `1:25` | `PSX/STAGE1/ROOM125.BSS` |
| `1:21` door 5 | room `1:26` | `PSX/STAGE1/ROOM126.BSS` |
| `2:00` door 1 | room `2:01` | `PSX/STAGE2/ROOM201.BSS` |
| `2:0B` door 1 | room `2:01` | `PSX/STAGE2/ROOM201.BSS` |

A door record alone would be weak evidence. A door record *plus* a room-scoped
background container for that exact room id, with no script anywhere on the disc,
is the shape of content that was planned, had art built for it, and was cut before
the room script existed. Four further records (`->2:02`, `->4:06`, `->5:13`) leave no
trace on the disc at all and are recorded as unresolved references rather than cut
content, because absence of evidence is not evidence of a cut room.

This is the kind of accounting stage 3 asks for: an entry is either verified or
evidenced as a limitation of the build, and "the room is referenced, has art, and has
no script" is evidence.
## Current accepted observations

The fresh runs at `out/gates/leon-thread-safe` and
`out/gates/elza-thread-safe` pass their scenario gates with the corrected
failure policy. Together they partially observe 40 files; 294 files remain
unverified. Stages 2–6 have zero entered functions. No file is marked fully
verified merely because it was read.

Elza entry checks character 4, positive health, room 103, the restored callback
and copy suffix, and the resident ROOM1031.RDT header. The header verifier
reconstructs the original runtime update of byte 0: guest 0x800392D4 reads the
selected camera mask, and 0x80039358 stores its high-word sprite count. The
rest of the header must match with relocation. A corrupted-header negative
control is rejected. `-VerifyOnly` rechecks saved evidence without rerunning
the native process.

The focused suite now passes 166 checks. Both runtime patches and curated
function maps reproduce the generated code deterministically. Raw game-derived
artifacts remain local and ignored.

## Model structure and inventory UI

The model index now structurally accounts for 102 blocks across all 54 EMS,
PLD and PLW files; see [model-inventory.md](model-inventory.md). This is not
an animation or playability pass.

Inventory open/close is verified separately. Item-panel navigation exposes
an unresolved rendering issue; see [inventory-status.md](inventory-status.md).
