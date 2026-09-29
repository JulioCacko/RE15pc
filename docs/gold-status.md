# Issues to gold

Every open item standing between the current state and gold, taken from the project's
own gates rather than from memory. Each entry names where its evidence lives, so a
claim here can be checked rather than trusted.

Gold is defined in `README.md` (Gold contract) and gated in `docs/phases.md`. Both say
the same thing: gold requires **verified gameplay, complete disc-content accounting,
persistence where implemented, stability, a clean reproducible build, and passing
release checks**, and **unknown behaviour blocks the relevant gate**.

Current standing: **phase 1 and phase 2 pass locally. Phase 3 is active. Phases 4 and 5
have not passed.**

---

## Phase 3 — all disc content (ACTIVE)

The gate: seven-overlay execution coverage and every disc-content entry verified or
evidenced as an original limitation, with no unknown entries.

| # | Issue | Where it stands |
|---|---|---|
| 3.1 | **294 of 334 disc files unverified**, 40 partially observed | The importer can promote a file only from unverified to *partial*. It cannot mark anything verified, and no file is verified merely because it was read. `content-coverage.md` |
| 3.2 | **Stages 2–6 have zero entered functions** | Five of seven overlays have never executed. This is now evidenced rather than unknown: room 117's door 1 is the *sole* gateway to stages 2–6 (removing it drops the reachable set from 27 rooms to 5), and its exit rectangle lies outside the room's walkable bounds from two independent approach lines. `content-coverage.md`, `room-graph.md` |
| 3.3 | **Two untested explanations for 3.2** | A scripted transition that fires without the player entering the rectangle, and room 117's second variant file `ROOM1171.RDT`, which no tool has examined. Until one is tested, "original limitation" is well-evidenced but not proven. `content-coverage.md` |
| 3.4 | **Branch coverage** | A function entry hit does not establish conditional-branch or path coverage. `content-coverage.md` |
| 3.5 | **Read ≠ correct** | A complete disc read does not prove decoding, presentation or interaction. `content-coverage.md` |
| 3.6 | **Packed-member accounting** | Animations, models, items, samples, sequences, background images and room-script branches need member-level accounting. `content-coverage.md` |
| 3.7 | **Unentered ≠ unreachable** | Mapped functions may include unused library routines or heuristic candidates; each exclusion needs its own evidence. `content-coverage.md` |
| 3.8 | **Configuration hygiene** | Runs from different configurations must not be combined into a release verdict. `content-coverage.md` |
| 3.9 | **Model semantics and animation** | 102 blocks across all 54 EMS/PLD/PLW files are accounted *structurally* (e.g. 25 sector-aligned blocks per CDEMD container). Semantic identity, animation coverage and member-level playability are not certified. `model-inventory.md`, `content-coverage.md` |
| 3.10 | **Six doors reference rooms with no script** | Rooms `1:23`–`1:26` and `2:01` each have a `.BSS` background container and no RDT. Recorded as cut content with evidence. `content-coverage.md` |
| 3.11 | **Four unresolved door references** | `->2:02` (twice), `->4:06`, `->5:13` leave no trace on the disc. Recorded as unresolved references, not cut content, because absence of evidence is not evidence of a cut room. `content-coverage.md` |
| 3.12 | **Two rooms have no usable door table** | `ROOM1060.RDT` and `ROOM1180.RDT` report an `init` pointer of 0. Named in the graph output, not yet explained. `room-graph.md` |
| 3.13 | **Two zero-area door records** | `3:06` door 3 and `3:08` door 0 parse as doors with a zero-area rectangle, so they can never be stood in. Excluded from the graph, not yet explained. `room-graph.md` |
| 3.14 | **Stage 6 unreachable** | 0 of 2 rooms reachable from the start, even before the door-1 exclusion. Unexplained. `room-graph.md` |

## Phase 4 — persistence and stability (NOT PASSED)

The gate: save/restart/load where implemented, protected corruption tests, normal
input/shutdown, and representative 30-minute sessions.

| # | Issue | Where it stands |
|---|---|---|
| 4.1 | **Save / load** | Not verified. Memory-card HLE exists (`Hardware/MemoryCard.cs`, `Runtime.CardA/CardB`, BIOS card events) but there is no gate proving a save survives a restart and reloads. `acceptance.md`, `phases.md` |
| 4.2 | **Corruption tests** | Protected tests that a corrupted or absent card is refused cleanly are outstanding. `phases.md` |
| 4.3 | **Normal input and shutdown** | Not certified end to end. `phases.md` |
| 4.4 | **Stability** | Representative **30-minute sessions have not been run**. `phases.md`, `README.md` |
| 4.5 | **Inventory rendering** | Item-panel navigation exposes an unresolved rendering issue; FILE content completeness is a separate open question. `inventory-status.md`, `content-coverage.md` |
| 4.6 | **Combat breadth** | One encounter and two actors are covered. Full encounter, enemy and weapon coverage remain open. `weapon-fire.md`, `phases.md` |
| 4.7 | **Native input certification** | Keyboard short taps are repaired, but unknown synthetic arrow events remain unverified and now fail acceptance instead of being swallowed. Directional input certification remains open. `keyboard-input.md` |
| 4.8 | **Turning against geometry** | The measured turn law (96 units/frame) is confirmed for free-standing turns and **unknown for turns taken while colliding**: one observation against a wall gave 42 units/frame. Recorded as a gap rather than smoothed over. `input-timing.md` |
| 4.9 | **Movement model gaps** | Heading-dependence of speed, whether the 8-frame start-up transient is heading-independent, turns taken while already moving, and holds longer than 64 frames are all unmeasured. `input-timing.md` |

## Phase 5 — gold release (NOT PASSED)

The gate: clean reproducible build, deterministic recompilation, complete
exact-revision acceptance, private publishing, and passing release checks.

| # | Issue | Where it stands |
|---|---|---|
| 5.1 | **Complete exact-revision acceptance** | Not met: it depends on phases 3 and 4. `phases.md` |
| 5.2 | **Release checks cannot run** | **GitHub Actions cannot start because of the account billing/spending limits.** `phases.md` states plainly: do not apply a gold tag while release checks are blocked. A local pass is not a remote check pass. |
| 5.3 | **Deterministic recompilation** | Passing - ten generated files reproduce byte-identically. Keep it green. `acceptance.md` |
| 5.4 | **Clean reproducible build** | Builds locally; `bootstrap.ps1` restores the pinned toolchain, and all 14 tracked patches were verified to apply in order to a fresh pin checkout and reproduce the working tree exactly. |
| 5.5 | **Publishing** | The private-publishing step in the gold gate has not been exercised. |
| 5.6 | **Overlay "skip" question** | Was listed as an open phase-5 question; `overlay-load-verbatim.md` resolves it - the loader copies each overlay verbatim, so `"skip"` stays 0. Closed, listed here only so it is not reopened. |

## Cross-cutting rules that also block gold

- **Never label an unexplained port failure an original defect.** Prototype defects may
  remain only *with supporting evidence*, which is why stage 2's unreachability is
  recorded with three measurements rather than asserted.
- **Do not invent missing prototype content.** An entry is either verified or evidenced
  as a limitation of the build.
- **Unknown behaviour blocks the relevant gate.** A gap is not a pass.

---

## The shortest path to gold

Ranked by what actually unblocks the most:

1. **Settle 3.2/3.3** — test the two remaining mechanisms for room 117's door 1. This
   either opens five overlays or converts the biggest phase-3 unknown into a fully
   evidenced limitation. Nothing else moves phase 3 as much.
2. **Run the 30-minute stability sessions (4.4)** — cheapest gate that is currently
   simply unrun rather than unsolved.
3. **Gate save/load (4.1, 4.2)** — the largest unimplemented feature area in phase 4.
4. **Resolve the release-check block (5.2)** — without it phase 5 cannot pass at all,
   regardless of the port.
5. **Work the 334-file ledger (3.1)** — mechanical, and the only route to "no unknown
   entries".
