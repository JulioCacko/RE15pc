# STAGE1 background readback repair

## Failure and cause

A full STAGE1 background decoded into 300 MDEC macroblocks (38,400 words).
The guest uploaded it as twenty 16x240 strips, then called StoreImage from
0x80021E44 to cache the 320x240 image at 0x80198000. The per-frame upload caller
0x800438B0 subsequently uploaded zeros from that RAM cache.

The interpolation backend queues guest commands until publication at VSync.
Its synchronous readback settled only the older current graph, omitting
queued graphs and the current recording. HleReadback copied those stale pixels
over the CPU VRAM shadow. This is a port ordering defect, not evidence of a
missing prototype background or an intentional fade.

## Repair

Patch 0008 routes guest VRAM reads through an explicit command barrier on the
GPU owner thread while the guest is blocked. It finishes the current graph,
queued graphs in order, and the unpublished recording before readback.
Consumed commands are cleared so subsequent publication cannot replay them.
Already fully composed current graphs are not replayed again.

Ordinary host inspectors retain the existing completed-frame read path.
The final host capture uses the guest barrier only after the guest has stopped.

The patch also supplies opt-in MDEC output and GPU load/store transfer counts.
No game-specific framebuffer replacement, skipped draws, RAM patch, or edit
to generated game code is involved.

## Reproduction and evidence

```powershell
dotnet build tests/RE15pc.Checks/RE15pc.Checks.csproj -c Release
dotnet run --project tests/RE15pc.Checks -c Release --no-build
pwsh -File tools/Test-Acceptance.ps1
pwsh -File tools/Test-RecompileDeterminism.ps1
dotnet run --project port/RE15pc -c Release --no-build -- --frames 2100 --timeout 120 --input "90:start,240:cross,300:cross,600:up:600,1250:cross,1800:up:240" --trace-input --verify-audio --log mdec,gpu
```

- Regression before the fix: `out/gpu-readback-before.log` fails at
  “GPU reads an upload made before the next VSync”.
- After: `out/gpu-readback-after.log`, all 40 checks pass, including queued
  ordering, no replay after publication, and no double execution after composition.
- Native harness: `out/gates/readback-fixed`, PASS including negative controls.
- Recompilation: all ten generated files byte-identical.
- Native run: `out/runs/readback-fixed-2100`, exactly 2100 frames, no runtime
  crash, unmapped call, or listener failure, and nonzero normal audio output.
- Computer Use directly observed the rooftop background in the native window.
- First background cache: 35,201 of 38,400 words nonzero. Following Up at
  completed frame 1800, two further 300-macroblock backgrounds were decoded
  and cached (29,497 and 31,443 nonzero words), followed by release at 2040.
- Equal-frame idle control: `out/runs/readback-fixed-idle-2100` remains at
  the initial rooftop camera with one background decode and 35,201 cached
  nonzero words. The otherwise identical Up run has three decoded backgrounds
  and a visibly different rooftop viewpoint, supporting movement/camera response.
- Early window close: `out/runs/window-close-negative` stopped at frame 790
  against a 100,000-frame target and returned FAIL/exit 1 with a consistent snapshot.

Raw logs, images, RAM, and disc-derived material remain ignored locally.
These facts demonstrate restored background readback and progression through
camera images. They do not complete collision, door, combat, inventory,
save/load, whole-disc coverage, or gold acceptance.
