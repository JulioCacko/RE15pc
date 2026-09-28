# Phase gates

Every phase ends in a gate that must actually pass before the next one starts. A phase is
not "done" because the work feels finished; it is done when the gate below it holds.

Legend: **DONE** · **PARTIAL** · **ACTIVE** · **TODO**

Current position: **Phases 0-3 and 5 done; Phases 4 and 6 partial; 7 and 8 not started.** The
port boots, runs real game code at 30 fps with **zero unmapped calls over 5414 guest frames**,
reaches STAGE1, dispatches all seven overlays and produces audio output. **The title renders at a
100.00% word-exact match and a room renders at 79.6% of the framebuffer in both buffers.**

**The remaining gap is play, not execution.** The game executes this build's opening sequence
correctly - title, a full room, a transition, and a slowly revealing state - and directions change
nothing at any point in it: 87 framebuffer generations with directions held against 86 without,
frame counts within 1.3%. So no room has been walked, no item taken, no door opened and no save made.
Two routes to close it are named in the README.

Two blockers stood in the way and both are now resolved or corrected. The composed frame was
**never** black - the room renders and the game fades itself out over it with flat subtract
rectangles, so `--skip-draws flat` removes the fade rather than repairing anything. And the guest
used to stop at frame 704 with an unmapped call, because an indexed dispatch table's entries had
been merged into their neighbouring functions; 33 computed-jump targets added to the main function
map take the same input to 5414 frames clean.

The mechanisms proposed and withdrawn along the way are listed in `docs/boot-status.md`.

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

- [x] Recompiler ran to completion: **5182 functions**, 44 SDK reimplementations applied. (5150 at the time this gate was written; 33 computed-jump targets were added to the main function map later, with the reason recorded under Phase 4.)
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
- [x] Guest progresses rather than wedging: 300 distinct memory states over 150 seconds, still
      changing at the sampling instant.
- [x] Runtime delivers interrupts: 2840 IRQ deliveries in 20 seconds; the guest is calling
      `VSync`, DMA channel 6, `PAD_dr` and `ChangeTh`.
- [x] The `title` overlay loads, and `stage1` loads after it - so the game is driven past the
      opening screens into gameplay, which takes scripted controller input because the title and
      character select both wait for a pad. See `--input` in `docs/boot-status.md`.
- [x] **All seven disc overlays dispatch correctly**, verified without needing gameplay:
      `--verify-overlays` exercises the whole dispatch path for every registered overlay and
      reports 8 of 8 with 0 failures. The seven disc overlays each have their start LBA mapped to
      their name and are promoted by a write into the first `0x800` bytes of their base, which is
      how the runtime detects that the guest has finished copying an overlay in; `main` has no LBA
      and is loaded by the entry point. Function counts match the configured maps exactly -
      stage1 664, stage2 520, stage3 626, stage4 492, stage5 628, stage6 43, title 38.
- [x] `TITLEJ.TIM` proven resident at the display origin at a **100.00% word-exact** match, and
      the title screen is a picture rather than noise: 41.4% non-black, 1123 distinct colours.
- [x] **RESOLVED - the composed frame is not black, and never was.** An earlier revision of this
      file called this the blocker and described the room's drawn output as never reaching video
      memory. Both were wrong, and the measurements that supported them were measurements of the
      wrong thing. The room renders at **79.6% of the framebuffer, in both buffers**, at frames 352
      and again at 576; the game then fades itself out by drawing large flat **subtract** rectangles
      over that correctly rendered image, which is what a fade to black is. `--skip-draws flat`
      removes those rectangles, so it was never an instrument for a rendering fault - it was a
      brightness control, and every conclusion drawn from it was a comparison between a faded frame
      and an unfaded one.
      The instruments that measured a black frame were measuring the fade correctly. The software
      store reasoning below is void for the reason already given: it receives only CPU writes and
      never sees drawn output at all.
- [x] **The real blocker - fixed.** The guest used to stop at frame 704 with an unmapped call at
      `0x800100AC`. The cause was a control-flow gap: an indexed dispatch table whose entries the
      function detector had merged into their neighbouring functions, so the guest jumping into the
      middle of `func_8001003C` found nothing to dispatch to. 33 computed-jump targets were added to
      `port/config/funcmaps/main.json` - deliberate curation of a generated file, since
      `--autoconfigure` would discard it. The same sustained input that previously stopped at frame
      704 now runs **5414 guest frames with zero unmapped calls** and a passing verdict.
      Phase 7 below anticipated this class of failure exactly: "linear sweep cannot recover every
      indirect jump, so unmapped calls surface one at a time as new code paths are reached". That is
      what happened, and the fix is the manual recovery the note implies.
- [ ] Title image is displaced 10 pixels horizontally, because the 20-byte TIM header is uploaded
      along with the pixels. Root cause not yet attributed.
- [ ] Animation not yet observed across frames, only individual frames compared.
- [x] **Audio produces output, which retracts the silence reported in the previous revision.** The
      SPU is enabled, unmuted and at half main volume; all 24 voices are used at some point; voice
      volume is written and peaks at 8597 of 32767, non-zero on 72 of 778 frames; and a block mixed
      *during* the run, while voices carried volume, peaks at 8619 of 32767. So the SPU generates
      sound rather than silence.
      The earlier "SILENT" verdict came from mixing a single block at the very end of the run, which
      can only report whether something happened to be playing at that instant, and the acceptance
      check itself repeated the mistake until it was changed to take its verdict from the during-run
      mix. That is the third time in this project that a measurement was right about *what* it
      sampled and wrong about *when* - the same mistake produced the "one-off erasure" reading of the
      framebuffer wipe, and then the audio verdict, and then the audio check that reported it.
- [ ] Continuous music is not established, and the measurement has improved without settling it.
      A 150-second run with mixed input now shows **all 24 voices carrying volume, non-zero on 2898
      of 4268 frames**, with 91 mixed blocks peaking at 16979 of 32767 - roughly half of full scale,
      and far more continuous than the earlier "intermittent, about a quarter" reading. Whether that
      is a music track or sustained sound effects is not distinguished, and CD audio is idle
      throughout (`cdAudio=False`, XA `playing=False`, `buffered=0`). Whether the disc's XA tracks
      ever stream is untested.

## Phase 5 — Overlay dispatch and determinism — DONE

- [x] Settled: overlays are copied into RAM **verbatim**, leading 4-byte word included, so
      `skip` is correctly 0. Proven by byte comparison against a live RAM dump, with STAGE1
      as a negative control. See `docs/overlay-load-verbatim.md`.
- [x] `stage1` vs `title` region overlap handled host-side; `OverlayPolicy` reports its
      evictions.
- [x] Recompiling twice produces byte-identical output: all 10 files in `generated/` match.
      Checked by `tools/Test-RecompileDeterminism.ps1`, which also fails correctly when a
      generated file is perturbed by hand. `generated/` may therefore stay ignored, which is
      the decision this gate existed to make.

The original open question was whether each `.BIN` is copied verbatim or has a leading word
stripped. It is verbatim. The decisive evidence was a live dump: RAM at `0x80100000` began
`0E 00 00 00 53 65 6C 65 63 74 68 33 2E 74 69 6D`, i.e. `TITLE.BIN`'s own leading word
followed by `Selecth3.tim`. Under a shift-by-4 rule the match would have been zero bytes; it
was 9924 of 9932, the single difference being a guest write at `0x801026C4`.

Determinism matters more than it looks. The recompiled C# is a pure function of the disc and
the configuration, and that is what makes a later change in game behaviour attributable to the
port rather than to recompiler drift. Run the gate after touching the configuration, the
function maps, or anything in `patches/`.

## Phase 6 — First playable room — PARTIAL

Room data (`.RDT` + `.BSS`), EMD models (`CDEMD0/1.EMS`), the player mesh (`PL00.PLD` with
`PL00W*.PLW`), doors (`DOOR00.DO2`), collision and camera.

**Gate:** a STAGE1 room renders at correct speed with a controllable player and working
doors.

- [x] A STAGE1 room renders: **79.6% of the framebuffer, 61135 of 76800 pixels, in both
      buffers**, at frames 352 and 576, and the game transitions out of it afterwards.
- [x] The game is interactive and waits for input. With none it holds the title screen
      indefinitely - two buffer changes in sixty seconds; with start and periodic cross
      plus held directions it makes **thirteen** state changes and moves to a different
      display. The comparison is fair: the frame counts differ by 7%.
- [ ] **A controllable player is not demonstrated.** No state has been observed in which
      a player character responds to the pad. The input that advances the game was found
      by sweeping buttons rather than by playing, and progress through a sequence is not
      the same as control of a character.
- [ ] **Doors are not demonstrated.** No door transition has been driven deliberately; the
      wipes observed at frames 598 and 820-822 are consistent with transitions but were
      not caused on purpose and their contents were not identified.

Phase 7 below anticipated this class of failure exactly: linear sweep cannot recover every
indirect jump, so unmapped calls surface one at a time as new code paths are reached.

## Phase 7 — Full game — TODO

Sound (`PSX/SOUND/*.BGM` SEQ banks, `*.VB`/`*.EDH` samples, XA streams), memory card save
and load, FMVs, all six stages, item data (`ITPS.ITP`), both scenarios, endings.

**Gate:** the acceptance criteria in `docs/compatibility.md`.

This is an iterative grind rather than a single fix: linear sweep cannot recover every
indirect jump, so unmapped calls surface one at a time as new code paths are reached.

## Phase 8 — Polish — TODO

Release build, settings persistence, HD texture replacement, widescreen and PGXP defaults,
port README.
