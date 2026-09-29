# Compatibility notes

What this port targets, what upstream RecompOne revision it is built against,
what has been changed and why, and what "complete" means for a build that was
never finished.

---

## Naming: RE15pc versus `bio2`

This project is named **RE15pc** after *Resident Evil 1.5*, the abandoned 1997
version of Resident Evil 2 that the community uses to refer to this material.

The disc itself does not use that name. Its own debug strings reference
`d:/bio2/psx/data/...` — Biohazard 2 — because that is what the developers were
building at the time. The community label and the disc's internal naming differ,
and this repository follows the community label for discoverability only. It is
not a claim about which build lineage the disc represents.

The disc is the **November 6, 1996** prototype, verified by hash in
`disc-manifest.json`.

---

## Upstream: RecompOne

| | |
|---|---|
| Repository | https://github.com/BlackLabelHQ/RecompOne |
| Pinned commit | `d81dec8c9622fdcd0865d73588a3baa8d3c3a605` |
| Licence | MIT, Copyright (c) 2026 flaffy |
| How it is obtained | `bootstrap.ps1`, never vendored |

The pin exists because a recompiler's output is a direct function of its
implementation. Moving the pin changes the generated C#, which would make
behavioural changes impossible to attribute. Bump it deliberately, re-run Phase
5's determinism check, and record the reason here.

### Patches

`bootstrap.ps1` applies every `patches/*.patch` in filename order with
`git apply --3way`, and fails loudly if one does not apply.

Twelve patches are now tracked. Patches 0001–0006 preserve the earlier GPU and crash diagnostics; patch 0007 adds controlled guest stopping, passive output observation, full-run log observation, and safe window-close reporting. Patch 0008 fixes synchronous guest VRAM readback across pending interpolation commands; see [background-readback.md](background-readback.md). Patch 0009 retains short keyboard taps and makes caught host-event exceptions fail acceptance; see [keyboard-input.md](keyboard-input.md). See [acceptance.md](acceptance.md). Patches 0010 and 0011 add optional entry/sector observations and propagate guest-thread failures. See [content-coverage.md](content-coverage.md). Patch 0012 discovers validated cross-image targets in unmapped gaps; see [weapon-fire.md](weapon-fire.md). Historical expectation: The expectation is that the SPU sequence player is the most
likely place one becomes necessary: `PSX/SOUND/*.BGM` are PSY-Q SEQ banks
driving VAB samples through the SPU, and `PSX.EXE` carries the strings
`Can't Open Sequence data any more` and `This is an old SEQ Data Format.`, which
suggests the game relies on the full SEQ driver.

RecompOne's README states that AI-generated pull requests are rejected and that
AI-produced ports are unsupported. Changes needed here are therefore kept as
patches against the pinned revision, not sent upstream. Landing anything
upstream would require a human to write and own that contribution.

---

## The disc

| | |
|---|---|
| File | `Bio2Nov96.bin` |
| Size | 124,300,848 bytes (52,849 × 2352) |
| SHA-256 | `B5C26B6A5EC21FC93B16F904642DAACD527207A7460FFB8349077AA6426BC30D` |
| Format | Raw MODE2/Form1, 2048 user bytes at offset 24 |
| Filesystem | ISO9660, PVD at LBA 16, root directory at LBA 22, 349 entries |

Verify any time with:

```powershell
pwsh -File tools/New-DiscManifest.ps1 -Check
```

### Cue sheet correction

The cue sheet shipped declaring `MODE0/2352`, which is wrong: every sector
carries the standard sync pattern and header mode byte `0x02`. Corrected to
`MODE2/2352`.

RecompOne happens to tolerate the wrong declaration, because `CueBinImage`
derives the user-data offset from the requested read size rather than the
declared mode whenever the sector size is 2352. Other tools do not — `chdman`
and reference emulators would read from offset 0 and find sync bytes where
ISO9660 expects a volume descriptor. The correction changes no byte of the
image.

### Boot executable

`SYSTEM.CNF` gives `BOOT = cdrom:PSX.EXE;1`, `TCB = 4`, `EVENT = 16`,
`STACK = 0X801FFF00`.

| | |
|---|---|
| `PSX.EXE` LBA / size | 34260 / 718,848 bytes |
| Entry PC | `0x80054448` |
| Load address | `0x80010000` |
| Text size | `0x0AF000` |
| Packed? | No — MIPS prologues are intact and debug strings are readable |

The build is a debug-oriented one and retains original source paths, which is
useful: `Stage Bin Size      : %X` at `0x80010DD8` is the overlay loader's own
output, and locating the code that references it is the fastest route to
settling the overlay header question in `docs/phases.md`.

### Overlays

Seven, all alternate RAM images of one region based at `0x80100000`:

| Name | File | LBA | Size |
|---|---|---|---|
| `stage1` | `PSX/BIN/STAGE1.BIN` | 25 | 137,648 |
| `stage2` | `PSX/BIN/STAGE2.BIN` | 93 | 103,216 |
| `stage3` | `PSX/BIN/STAGE3.BIN` | 144 | 129,036 |
| `stage4` | `PSX/BIN/STAGE4.BIN` | 208 | 108,064 |
| `stage5` | `PSX/BIN/STAGE5.BIN` | 261 | 133,280 |
| `stage6` | `PSX/BIN/STAGE6.BIN` | 327 | 10,324 |
| `title` | `PSX/BIN/TITLE.BIN` | 333 | 9,932 |

They are **not** PS-X EXEs: they carry no `PS-X EXE` header.

**How the base was established.** Every overlay's embedded pointer table
references addresses in the `0x8010xxxx`–`0x8011Bxxx` range almost exclusively,
alongside a smaller set pointing into the main executable around
`0x8004F000` and `0x800Bxxxx`. The largest extent is `0x219B0` (STAGE1), which
puts the whole region at `0x80100000`–`0x801219B0`, comfortably below the stack
at `0x801FFF00` and clear of the main executable at `0x80010000`–`0x800BF000`.

**Overlay names are significant.** RecompOne keys overlay dispatch on
`CdUtils.OverlayName`, which is the lowercase basename without extension. A
config that names an overlay anything else will compile but never load. This is
why the manifest emits names in exactly that form.

### One shared region has a consequence

Because all seven overlays occupy the same base, RecompOne's default region
bookkeeping is not sufficient on its own. `Dispatcher.HandleRegionOverwrites`
retires an active overlay only when the newly loaded one fully *covers* it. The
`title` overlay is 9,932 bytes against `stage1`'s 137,648, so loading `title`
after `stage1` leaves `stage1`'s functions mapped above `0x801026CC` while the
RAM they refer to has been overwritten — nondeterministic corruption at stage to
title transitions.

`port/RE15pc/OverlayPolicy.cs` handles this host-side, by unloading any active
overlay that shares the incoming overlay's base. That is a generalisation of
upstream's containment rule to the exact-containment case this game uses, and it
avoids forking the runtime.

---

## What "complete" means here

The November 1996 build is an unreleased prototype that Capcom cancelled and
restarted from scratch. It is not content-complete, and no amount of work in
this repository will restore content that was never authored into it.

**Complete therefore means: every asset and code path present on this disc is
reachable and playable.** Specifically:

- Boot, Capcom FMV, title and character select, unattended.
- Every room that has an `.RDT` loads, with door transitions, player movement,
  interaction, and enemies that spawn and die.
- Items pick up; inventory opens; combination works.
- Music and SFX play; FMVs decode.
- Memory card save, load, and mid-game resume.
- An ending sequence is reachable.
- A 30-minute session runs at correct frame pacing with no crash and no growth
  in the unmapped-call set.

Whether a given character, enemy or room exists in *this* build is determined by
enumerating what the disc actually contains — `PL00/01/02/04/05/06/0D/0E/0F.PLD`
for characters, `CDEMD0/1.EMS` for models and enemies, the per-stage `ROOM*.RDT`
set for rooms — not by what earlier or later revisions of Resident Evil 2
contained. Anything found to be present but unimplemented is recorded in the
gaps table below rather than described as working.

---

## Known gaps

The first-room gate passes; full gameplay coverage remains unverified. See [acceptance.md](acceptance.md) for current evidence; the older phase summaries are not gold acceptance.

| Subsystem | Status | Notes |
|---|---|---|
| | | |

---

## Explicitly out of scope

**Console targets — Nintendo 64, Saturn, 3DO, Atari Jaguar.** RecompOne emits
C# and its runtime is a Silk.NET / ImGui / OpenAL desktop layer. It cannot
produce code for those machines, and no configuration of this repository will
change that. Reaching them would require a separate backend emitting portable C
plus hand-written per-console GPU, SPU and CD layers — a different project that
could reuse the analysis and configuration here, but not the runtime.
