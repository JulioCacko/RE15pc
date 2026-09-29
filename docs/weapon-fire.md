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

Enemy health differs in the final combat dump, but damage and kill acceptance
remain open until they are tracked within the same controlled run. This gate
does not certify all weapons, enemy types, hit reactions, kills or reload paths.

The final build passes 166 focused checks and the full Leon first-room
regression at `out/gates/leon-cross-image-regression`. All twelve runtime/
recompiler patches reproduce the working upstream tree.
