# Phase gates

These gates supersede the earlier milestone interpretations in the historical
boot investigation. A smoke pass does not complete a gameplay gate.

| Phase | Status | Gate |
|---|---|---|
| 1. Trustworthy acceptance | Passed locally | Exact guest-frame stopping, all requested checks aggregated, persistent failures, passive audio, isolated evidence and saves, negative controls |
| 2. First playable room | Active | Cold boot, deliberate movement and turning, collision/camera, one intentional door transition, visible and state evidence |
| 3. All disc content | Not passed | Seven-overlay execution coverage and every disc-content entry verified or evidenced as an original limitation; no unknown entries |
| 4. Persistence and stability | Not passed | Save/restart/load where implemented, protected corruption tests, normal input/shutdown, representative 30-minute sessions |
| 5. Gold release | Not passed | Clean reproducible build, deterministic recompilation, complete exact-revision acceptance, private publishing and passing release checks |

## Phase 1 evidence

See [acceptance.md](acceptance.md). The local native suite passed at
`out/gates/harness-v2`; the focused suite now has 33 passing checks.
Recompilation compared all ten generated files byte-for-byte successfully.
All seven tracked patches exactly reconstruct the current upstream changes.

## Phase 2 evidence

A 2100-frame run reached STAGE1 around frame 1470 following Cross at frame
1250. The held direction at frames 600–1199 happened before gameplay, so it
cannot demonstrate player movement. Computer Use directly observed character
selection. At the end of the run a small character rendered against a black
background. The background and control behavior remain under investigation.

Circle at frames 240 and 420 left the game at character selection through
frame 1200. Menu inputs must be tied to observed game state rather than inferred
from framebuffer occupancy.

## Preservation rules

No reconstructed content, HD replacement textures, widescreen redesign, or
Linux certification is required for this Windows milestone. Original prototype
defects may remain only with supporting evidence. Never label an unexplained
port failure an original defect. No emulator path is a prerequisite.

The GitHub commit workflow currently cannot start because of account
billing/spending limits. Do not apply a gold tag while release checks are blocked.
