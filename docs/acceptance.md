# Acceptance harness

The harness gate is separate from gameplay acceptance. Passing a run means the
requested checks passed; it does not prove that a displayed scene is gameplay.

## Reproduce

```powershell
dotnet build tests/RE15pc.Checks/RE15pc.Checks.csproj -c Release
dotnet run --project tests/RE15pc.Checks -c Release --no-build
pwsh -File tools/Test-Acceptance.ps1
pwsh -File tools/Test-RecompileDeterminism.ps1
```

The native gate tests two exact 180-frame runs with all 49 ordered overlay
replacement pairs, an audio-negative startup run at frame 1, a wall timeout,
and rejection of a nonempty output directory. The focused checks include
failure aggregation, log rollover, input boundaries, stale overlay eviction,
resident functions, and restoration of the active maps.

Latest local gate: `out/gates/harness-v2`, PASS. The focused suite had 33 passing checks at that checkpoint; the GPU readback
regression raises the current total to 40, with the native gate passing again
at `out/gates/readback-fixed`.
Both positive native cases stopped at exactly 180 frames with exit 0.
The audio-negative case stopped at frame 1 with exit 1; the timeout case
failed at frame 0 with exit 1. All 10 generated files remained byte-identical.
These artifacts are local and ignored, so reproduce them after cloning.

## Running scenarios

```powershell
dotnet run --project port/RE15pc -c Release --no-build -- --frames 2100 --timeout 120 --input "90:start,240:cross,600:up:600,1200:none" --trace-input --verify-audio
```

- `--frames N` stops at exactly N completed guest VSync calls. These are game
  frame boundaries, not monitor presents or hardware VBlanks.
- `--timeout S` bounds a frame run, default 300 seconds. Timeout is failure.
- `--smoke S` retains duration-based runs; it cannot be combined with `--frames`.
- Input is `frame:buttons[:duration]`. A legacy press lasts 12 frames.
  The latest step replaces previous holds; expiry releases all buttons.
  `none` releases explicitly. Scripted port 1 is released between steps,
  so physical keyboard state cannot contaminate the script.
- The frame number denotes the completed VSync; its step is delivered on the
  next BIOS pad poll. `--trace-input` records the actual substituted BIOS values.
- Default output is a unique folder below `out/runs`. An explicit `--out`
  must be empty. Bounded runs copy settings and memory cards into that folder,
  including cards configured with absolute paths, and operate on those copies.

## Evidence and limitations

`run.json`, `verdict.txt`, and the process exit agree on the aggregate result.
The JSON includes revision, dirty status, disc verification status, configuration
and patch hashes, requested checks, exact completed frame count and evidence.
Failures survive the runtime's 4,000-line UI log rollover.

The guest stops cooperatively outside the event bus, at a frame boundary;
the host joins it and stops the audio mixer before inspecting RAM or dispatch.
GPU readback and cleanup run on the GL owner thread. A guest that cannot
stop within the 15-second grace period fails without a purported final snapshot.

Audio checks observe PCM already queued by the normal output path. They never
call the SPU mixer. Nonzero PCM is not proof of correct music or uninterrupted
playback. The title is not assumed silent: an undriven 60-frame run produced
nonzero output, so the negative case uses the measured silent first frame.

Synthetic overlay checks do not execute guest functions. Gameplay coverage
must be demonstrated independently. Non-strict dispatch and diagnostic
rendering overrides cannot produce a preservation acceptance pass.

## Current gameplay evidence

Computer Use inspection of the native 2100-frame scripted run showed the
“PLEASE SELECT MAIN CAST” screen with Leon and Elza. Previous pixel counts
described as a playable room do not identify a controllable gameplay state.
The STAGE1 background has since been repaired and verified in the native
window; see [background-readback.md](background-readback.md). No complete
movement/collision/door, inventory, combat or save/load gate is established.

GitHub Actions currently cannot start because of the account billing/spending
limit. Local checks do not substitute for a passing required remote release check.
