# RE15pc

A Windows PC preservation port of the **Biohazard 2 November 6, 1996 prototype**
(commonly called Resident Evil 1.5), built by statically recompiling the disc's
MIPS executable and seven overlays with [RecompOne](https://github.com/BlackLabelHQ/RecompOne).

**Current status: Leon’s first-room gate and Elza’s entry gate pass; gold remains open.**
The native host boots, renders the rooftop, turns and moves Leon, respects the
railing collision, and deliberately exits from room 117 into room 116.
The destination room data and player spawn are checked against the actual disc.
The full-disc ledger has 37 partially observed files and 297 unverified files.
See [coverage and remaining scope](docs/content-coverage.md),
[first-room acceptance](docs/first-room.md), the
[background repair](docs/background-readback.md), and
[native input evidence and limits](docs/keyboard-input.md).

## Build

Requires Git, PowerShell, .NET SDK 10, and your own `Bio2Nov96.bin` with
`Bio2Nov96.cue` beside it. The disc is identified by the hash and layout in
`disc-manifest.json`; it is not included.

```powershell
pwsh -File bootstrap.ps1
dotnet run --project port/RE15pc -c Release -- --cue .\Bio2Nov96.cue
```

The bootstrap pins RecompOne at
`d81dec8c9622fdcd0865d73588a3baa8d3c3a605` and applies tracked patches.
Do not use `-Force` on a working upstream clone containing unaccounted changes.
Do not regenerate the curated function maps with `--autoconfigure`: that would
discard recovered computed-jump targets.

## Verify

```powershell
dotnet build tests/RE15pc.Checks/RE15pc.Checks.csproj -c Release
pwsh -File tools/Test-Acceptance.ps1
pwsh -File tools/Test-FirstRoom.ps1
pwsh -File tools/Test-ElzaEntry.ps1
pwsh -File tools/Test-RecompileDeterminism.ps1
pwsh -File tools/New-DiscManifest.ps1 -Check
```

The 159 focused checks and native gates cover exact frame counts, failure propagation, all 49
ordered overlay replacements, input holds/releases, log rollover, timeouts, and
isolated output. Recompilation currently produces ten byte-identical files.

For a scripted run:

```powershell
dotnet run --project port/RE15pc -c Release --no-build -- --frames 2100 --timeout 120 --input "90:start,240:cross,300:cross,600:up:600,1250:cross,1800:up:240" --trace-input --verify-audio
```

This is an investigation scenario, not a gameplay acceptance test.
`--frames` counts completed guest VSync calls. `frame:buttons:duration` holds
buttons for that many frames; omitted duration means 12. `none` releases them.

Each bounded run writes to a unique directory under `out/runs` unless an empty
`--out` directory is supplied. Settings and memory cards are copied into the run
directory to protect player state. `run.json`, `verdict.txt`, and the process
exit contain the aggregate result. Audio checks inspect normal queued PCM;
they do not advance the mixer to manufacture a diagnostic sample.

See [acceptance.md](docs/acceptance.md) for the evidence and its limits,
[phases.md](docs/phases.md) for progress, and
[compatibility.md](docs/compatibility.md) for disc and overlay details.
[boot-status.md](docs/boot-status.md) is a historical investigation log; its
earlier interpretations are not current acceptance claims.

## Gold contract

Windows x64 first, strict preservation. Fix port-induced defects without
inventing missing prototype content. Gold requires verified gameplay, complete
disc-content accounting, persistence where implemented, stability, a clean
reproducible build, and passing release checks. Unknown behavior blocks the
relevant gate. Emulator setup is not required.

Synthetic dispatch success, nonzero audio, pixel counts, and a clean smoke run
do not establish playability. Movement, collision and one intentional exit are verified in the first-room
gate. Inventory open/close is verified, while item-panel rendering remains under investigation.
Full-room/scenario coverage, remaining interactions and combat, inventory operations,
save/load, full native-input certification and the stability gate remain open.
GitHub Actions is currently blocked from starting by account billing/spending
limits; local passes do not establish a remote check pass.

## Repository rules

All commits follow [Conventional Commits v1.0.0](https://www.conventionalcommits.org/en/v1.0.0/),
enforced by the hook and workflow described in [CONTRIBUTING.md](CONTRIBUTING.md).

Never commit the disc, `generated/`, `out/`, saves, or the separate
`RecompOne/` clone. Generated code and compiled game-derived binaries are
created locally from the user's disc. Runtime/recompiler changes are delivered
as patches against the pinned upstream, never edits to generated game code.

This unofficial preservation project is not affiliated with Capcom.
Resident Evil and Biohazard are Capcom trademarks. No Capcom game code, art,
audio, disc image, or Sony BIOS is redistributed. See [NOTICE](NOTICE).

Current structural and UI evidence: [model inventory](docs/model-inventory.md)
and [inventory status](docs/inventory-status.md).
