# Aimed firing and cross-image entry repair

The controlled Elza route equips weapon 8, aims, fires twice, and returns to
normal control. Evidence: `out/runs/elza-shotgun-gap-fixed`.

```powershell
pwsh -File tools/Test-WeaponFire.ps1 -RunDir out/runs/elza-shotgun-gap-fixed
```

The gate checks the exact route and frame count, weapon identity, positive
runtime/audio results, ammunition 7 -> 6 -> 5, unchanged unrelated slots,
preserved player health, and execution of the repaired entry at 0x80066A1C.
A negative control with no ammunition consumption is rejected.

## Root cause

The enemy reaction path at 0x80108364 calls 0x80066A1C in the main image.
That short entry loads a 32-bit vector and branches into the short-result
normalization tail. It lies between two existing mapped SDK functions.

The cross-image scanner previously required targets to fall inside an existing
function body. It therefore missed valid code in gaps. Patch 0012 removes that
restriction and filters discovered candidates through the existing supported
instruction validator. An initial unfiltered experiment exposed data bytes as
candidates and was discarded before acceptance.

The synthetic regression proves discovery of a gap entry, stable rescanning,
and rejection of unsupported-instruction candidates. The validated disc build
adds only the two required main-image entries, for 5192 registered functions
across all eight images. Regenerated output remains deterministic.

Before the repair, the firing route failed at guest frame 3262 with one unmapped
call. After it, the run reaches exactly 3500 frames without a crash, unmapped
call or listener failure.

## State interpretation

The control-blocked bit at 0x800ACA3C is also set while aiming. Inventory state
now uses the original menu mode at 0x800B5359 instead: modes 1–5 are menu work,
zero is gameplay. This prevents aiming from being reported as an open menu.

The separate within-run damage/removal gate below now tracks original actors
through damage and removal. Neither gate certifies all weapons, enemy types,
hit reactions, or reload paths.

The final build passes 166 focused checks and the full Leon first-room
regression at `out/gates/leon-cross-image-regression`. All twelve runtime/
recompiler patches reproduce the working upstream tree.

## Within-run damage and removal

`out/runs/elza-damage-pool` reaches exactly 4000 guest frames with no runtime
faults. A second cold boot at `out/runs/elza-damage-replay` passes the same
gate and frame interval. It extends the equipment route with R1 at frame 3200, R1+Square held
from 3260 through 3679, then release. The read-only sampler records all twenty
0x1F4-byte actor slots at 0x800ACC2C; the original traversal bounds at
0x800372B4 and 0x800428B0 establish this pool size. The live counts are not
high-water slot indexes and must not truncate the capture.

```powershell
pwsh -File tools/Test-EnemyDamage.ps1 -RunDir out/runs/elza-damage-pool -TestRejections
```

The gate requires passing runtime/audio/coverage checks, exact delivered
combat inputs, complete samples from frames 3195 through 4000, stable
slot/kind/spawn identities, and execution of the terminal/removal handlers.
Five in-memory negative controls reject unchanged health, a still-active
actor, a reused identity, a truncated pool, and absent ammunition consumption.
They never modify the recorded run or memory cards.

| Frame | Ammunition | Observed original actor state |
|---|---|---|
| 3195 | 7 | Slots 0–5 active; health 81, 99, 97, 87, 89, 81 |
| 3270 | 6 | Slots 1/2 reach -1; slots 4/5 fall to 49/41 |
| 3420 | 2 | Slots 1/2/4 reset to 30 and remain active |
| 3540–4000 | 0 | Slots 0/5 have health -39, flags 0, terminal state 0x2060107 |

Negative health alone is not a kill criterion: original code at 0x80108970
sets health to 30. Terminal handler 0x80109554 calls 0x80039A74, whose store
at 0x80039AE8 clears actor flags. The run enters both handlers and 0x8004267C,
which can initialize another pool record. Slots/spawn IDs 6 and 7 become
active during this encounter and are accounted for separately. Thus this
gate establishes damage and persistent removal of two original actors in
one scenario; it does not establish that the encounter is cleared. Function
entry coverage does not prove per-actor call association or branch coverage.
