# Phase gates

Every phase ends in a gate that must actually pass before the next one starts.
A phase is not "done" because the work feels finished; it is done when the gate
below it holds. Status is updated here as gates pass.

Legend: **DONE** · **ACTIVE** · **TODO**

---

## Phase 0 — Repository and disc hygiene — DONE

- [x] Repository scaffolded with the disc image and the upstream RecompOne clone
      excluded before the first commit.
- [x] `Conventional Commits v1.0.0` hook installed at `.githooks/commit-msg` and
      verified to reject a non-conventional header, a capitalised description, an
      unknown type, and a lowercase `BREAKING CHANGE`.
- [x] Private repo `github.com/JulioCacko/RE15pc` created and pushed.
- [x] `Bio2Nov96.cue` track mode corrected from `MODE0/2352` to `MODE2/2352`.
- [x] `disc-manifest.json` generated and cross-checked against an independent
      SHA-256.
- [x] `bootstrap.ps1` pinned RecompOne at `d81dec8` and reported a clean tree.

**Gate:** the manifest verifies with `-Check`, and `bootstrap.ps1` reproduces the
toolchain. Both hold.

---

## Phase 1 — Toolchain and disc probe — ACTIVE

- [ ] `RecompOne.sln` builds in Release.
- [ ] `recompone --probe-disc Bio2Nov96.cue -json out/probe.json -all` classified
      every one of the 349 entries.

**Gate:** boot resolves to `PSX.EXE`; `STAGE1..6.BIN` and `TITLE.BIN` classify as
code with base `0x80100000`; the full classification table is archived,
including the false positives Phase 2 intends to prune.

**Why this gate matters:** `DiscProbe` classifies by code-density score plus an
extension allowlist. `PSX/EMD/*.EMS`, `PSX/ITEM/*.ITP`, `PSX/PLD/*.PLD`,
`*.PLW`, `PSX/STAGE*/*.RDT`, `*.BSS` and `PSX/SOUND/*.BGM` match neither the
media list nor obviously-readable code, so some will be misread as code and
swept as overlays. Knowing exactly which ones, before writing the config, is the
difference between a curated config and megabytes of garbage functions.

---

## Phase 2 — Curated recompiler configuration — TODO

- [ ] `--autoconfigure` run to produce a baseline.
- [ ] `port/config/bio2nov96.json` reduced to exactly seven overlays.

**Gate:** `recompone port/config/bio2nov96.json` exits 0 with no skipped-overlay
warnings and plausible per-overlay function counts.

The configuration is a hand-curated artefact, not generated output. Autoconfigure
is a starting point that gets edited down; see `port/config/bio2nov96.json`.

---

## Phase 3 — Recompile and host application — TODO

- [ ] Recompiler runs to completion; `generated/Entry.cs` registers all seven
      overlays and calls the entry point at `0x80054448`.
- [ ] `port/RE15pc` host application builds and opens a window.

**Gate:** `[Dispatcher] loaded overlay: main` appears and the process survives a
frame without crashing.

---

## Phase 4 — Boot to title — TODO

**Gate:** `CAPCOM.STR` decodes and plays; the title screen renders and animates;
**zero** unmapped calls in a 60-second run with `Dispatcher.Tolerant = false`.

---

## Phase 5 — Overlay dispatch and determinism — TODO

- [ ] Settle whether each `.BIN` is copied verbatim to `0x80100000` or whether the
      loader skips the leading 4-byte word.
- [ ] Confirm `OverlayPolicy` retires `stage1` when `title` loads.
- [ ] Recompile twice and compare output.

**Gate:** RAM at `0x80100000` matches the overlay file under the decided rule;
all seven overlays log load/unload correctly; two consecutive recompiles produce
byte-identical `generated/`.

**Open question, and how it gets answered.** Every overlay begins with a small
word — STAGE1 `0x0000000F`, TITLE `0x0000000E`, STAGE6 `0x00000014` — followed by
code or data. Whether that word is part of the RAM image decides whether the
overlays need `"skip": 4`. The test is direct: dump RAM at `0x80100000`
immediately after the first `stage1` load and compare against
`PSX/BIN/STAGE1.BIN`.

- If `ram[0x1000..] == file[0x1000..]` but the first words differ, the loader
  strips a header and every overlay needs `"skip": 4`. `LbaStart` is unaffected,
  since `(0 + 4) / 2048 == 0`.
- If they match entirely, `skip` stays 0.

There is a shortcut worth trying first: find the code that references the
`Stage Bin Size      : %X` debug string at `0x80010DD8` in `PSX.EXE`, which is
the overlay loader, and read what it does with the first word.

---

## Phase 6 — First playable room — TODO

Room data (`.RDT` + `.BSS`), EMD models (`CDEMD0/1.EMS`), the player mesh
(`PL00.PLD` with `PL00W*.PLW`), doors (`DOOR00.DO2`), collision and camera.

**Gate:** a STAGE1 room renders at correct speed with a controllable player and
working doors.

---

## Phase 7 — Full game — TODO

Sound (`PSX/SOUND/*.BGM` SEQ banks, `*.VB`/`*.EDH` samples, XA streams), memory
card save and load, FMVs, all six stages, item data (`ITPS.ITP`), both
scenarios, endings.

**Gate:** the acceptance criteria in `docs/compatibility.md`.

This is where most of the remaining work lives, and it is an iterative grind
rather than a single fix: linear sweep cannot recover every indirect jump, so
unmapped calls surface one at a time as new code paths are reached.

---

## Phase 8 — Polish — TODO

Release build, settings persistence, HD texture replacement via
`AssetReplacerManager`, widescreen and PGXP defaults, port README.
