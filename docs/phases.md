# Phase gates

Every phase ends in a gate that must actually pass before the next one starts. A phase is
not "done" because the work feels finished; it is done when the gate below it holds.

Legend: **DONE** · **PARTIAL** · **ACTIVE** · **TODO**

Current position: **Phase 4, partially met.** The port boots and runs real game code; one
rendering defect is identified. See `docs/boot-status.md` for the evidence.

---

## Phase 0 — Repository and disc hygiene — DONE

- [x] Repository scaffolded with the disc image and the upstream RecompOne clone excluded
      before the first commit.
- [x] Conventional Commits v1.0.0 hook at `.githooks/commit-msg`, verified to reject a
      non-conventional header, a capitalised description, an unknown type, and a lowercase
      `BREAKING CHANGE`.
- [x] Private repo `github.com/JulioCacko/RE15pc` created and pushed.
- [x] `Bio2Nov96.cue` track mode corrected from `MODE0/2352` to `MODE2/2352`.
- [x] `disc-manifest.json` generated and cross-checked against an independent SHA-256.
- [x] `bootstrap.ps1` pins RecompOne at `d81dec8` and reports a clean tree.

## Phase 1 — Toolchain and disc probe — DONE

- [x] `RecompOne.sln` builds in Release.
- [x] All 349 entries classified: 1 executable, 7 code, 69 media, 257 data.

**Gate deviation, resolved.** The gate asked for all seven overlays to be classified at
base `0x80100000`. Two were not, and the reasons are upstream heuristic limits rather than
disc problems: `STAGE6.BIN` was guessed as `0x8004F000`, which lies *inside* the resident
main executable (`0x80010000` + `0x0AF000` = `0x800BF000`) and so cannot be a load address;
`TITLE.BIN` got no base at all, because its 18 in-range pointers fall below `GuessBase`'s
`bestHits >= 32` confidence threshold, and `AutoConfigurator` silently drops an overlay it
cannot base. The expected false positives did *not* occur - every EMS, ITP, PLD, PLW, RDT,
BSS, BGM and DO2 file classified as data. Full table in `docs/probe-classification.md`.

## Phase 2 — Curated recompiler configuration — DONE

- [x] `--autoconfigure` produced the baseline and the function maps.
- [x] `port/config/bio2nov96.json` reduced to exactly seven overlays with bases declared
      explicitly rather than accepted from the guesses.
- [x] `stage6` and `title` maps regenerated at the correct base. This was necessary, not
      cosmetic: `FunctionMapLoader.Load` reads stored addresses **verbatim** and does not
      rebase them, so fixing the config's base alone would not have fixed a map swept at
      the wrong one.

## Phase 3 — Recompile and host application — DONE

- [x] Recompiler ran to completion: **5150 functions**, 44 SDK reimplementations applied.
      `pointerScan` added 780 entry points to main (1359 → 2139), which is why it is on.
- [x] `generated/Entry.cs` registers all eight tables and calls `0x80054448`.
- [x] `port/RE15pc` builds and opens a GL 4.5 window.
- [x] `OverlayPolicy` unloads equal-base overlays on load.
- [x] Process exit code made meaningful by terminating rather than unwinding.

**Gate met:** `[Dispatcher] loaded overlay: main` appears and the process survives frames.

## Phase 4 — Boot to title — PARTIAL

- [x] Guest code executes: `ResetGraph:jtb=8007e308,env=8007e350` is guest output, not
      runtime output.
- [x] **Zero unmapped calls** in a 60 second run with `Dispatcher.Tolerant = false`.
- [x] Guest progresses rather than wedging: 48 distinct memory states over 25 seconds, last
      change at the sampling instant.
- [x] Runtime delivers interrupts: 2840 IRQ deliveries in 20 seconds; the guest is calling
      `VSync`, DMA channel 6, `PAD_dr` and `ChangeTh`.
- [x] The `title` overlay loads.
- [x] The framebuffer is a picture, not noise: 41.4% non-black, 1123 distinct colours.
- [x] `TITLEJ.TIM` proven resident at the display origin at **100.00% word-exact** match.
- [ ] **Title image is displaced 10 pixels horizontally** because the 20-byte TIM header was
      uploaded along with the pixels. Root cause not yet attributed.
- [ ] Animation not yet observed across frames, only a single frame compared.
- [ ] Audio not proven to play at all.

## Phase 5 — Overlay dispatch and determinism — PARTIAL

- [x] Settled: overlays are copied into RAM **verbatim**, leading 4-byte word included, so
      `skip` is correctly 0. Proven by byte comparison against a live RAM dump, with STAGE1
      as a negative control. See `docs/overlay-load-verbatim.md`.
- [x] `stage1` vs `title` region overlap handled host-side; `OverlayPolicy` reports its
      evictions.
- [ ] Recompile twice and confirm byte-identical `generated/` output. Not yet done, and it
      is what decides whether `generated/` may stay ignored.

The original open question was whether each `.BIN` is copied verbatim or has a leading word
stripped. It is verbatim. The decisive evidence was a live dump: RAM at `0x80100000` began
`0E 00 00 00 53 65 6C 65 63 74 68 33 2E 74 69 6D`, i.e. `TITLE.BIN`'s own leading word
followed by `Selecth3.tim`. Under a shift-by-4 rule the match would have been zero bytes; it
was 9924 of 9932, the single difference being a guest write at `0x801026C4`.

## Phase 6 — First playable room — TODO

Room data (`.RDT` + `.BSS`), EMD models (`CDEMD0/1.EMS`), the player mesh (`PL00.PLD` with
`PL00W*.PLW`), doors (`DOOR00.DO2`), collision and camera.

**Gate:** a STAGE1 room renders at correct speed with a controllable player and working
doors.

## Phase 7 — Full game — TODO

Sound (`PSX/SOUND/*.BGM` SEQ banks, `*.VB`/`*.EDH` samples, XA streams), memory card save
and load, FMVs, all six stages, item data (`ITPS.ITP`), both scenarios, endings.

**Gate:** the acceptance criteria in `docs/compatibility.md`.

This is an iterative grind rather than a single fix: linear sweep cannot recover every
indirect jump, so unmapped calls surface one at a time as new code paths are reached.

## Phase 8 — Polish — TODO

Release build, settings persistence, HD texture replacement, widescreen and PGXP defaults,
port README.
