# Boot status

Where the port actually is, what has been proven, and what is known to be wrong.
Everything below is backed by an artifact on disk or a command that can be re-run.

Last updated at the end of Phase 5.

---

## Summary

The recompiled prototype **boots, runs, and now plays through its opening screens into
STAGE1**. Guest code executes, receives interrupts, decodes images through MDEC, loads the
`title` overlay and is driven by scripted controller input past the title and character
select until the `stage1` overlay loads. There are **zero unmapped calls**.

The immediate blocker is that the composed frame comes out **black**. A decisive experiment -
turning the GPU HLE off with `--software-gpu` - produces a complete, correct VRAM image
(90.4% non-black, 1140 distinct colours) in the software store, with **byte-identical texture
content in both modes**. That rules out the guest code, disc reads, MDEC decoding and texture
uploads at once, and localises the defect to the HLE's deferred recording layer: under the HLE,
the content destined for the framebuffer never arrives. Details, evidence and the next three
probes are below.

One rendering defect is also identified and quantified: before the stage load, the title
image was uploaded to VRAM **including its 20-byte TIM header**, displacing the picture
10 pixels horizontally.

---

## STAGE1 loads

Scripted input changed what is reachable. Without it the port could only ever reach the
first screen that waits for a pad, which made every later phase untestable.

```
[Dispatcher] loaded overlay: main
ResetGraph:jtb=8007e308,env=8007e350
[Dispatcher] loaded overlay: title
[Dispatcher] loaded overlay: title overwritten by stage1
[Dispatcher] loaded overlay: stage1
```

`overlays loaded : 3`, `region overwrites : 1`. The overwrite line is upstream's own doing
and is correct here: `stage1` (137,648 bytes) fully covers `title` (9,932 bytes), so
`Dispatcher.HandleRegionOverwrites` retires it without help. `OverlayPolicy` reports zero
evictions, which is the expected outcome — it exists for the case upstream cannot handle,
and this was not one.

Verifying the overlay really is resident: the dumped overlay window now begins
`0F 00 00 00 9C FF FF FF A8 FD FF FF`, which is `PSX/BIN/STAGE1.BIN` from offset 0, not the
title image. Consistent with the verbatim-copy finding.

### A frame-rate correction worth recording

An earlier reading of these runs concluded the guest had stalled around frame 1528 because
`VSyncEvent` stopped advancing at roughly half the expected 60 per second. That was wrong.

`LibEtc.VSync` returns early for `mode < 0` and for `mode == 1` **without dispatching
`VSyncEvent`**, and `WaitVBlanks` waits `mode` vblanks. This game calls `VSync(2)`, which
waits two vblanks, so 30 events per second is exactly correct for a 30 fps game. A 150 second
run reached frame 4527, i.e. 30.2 events per second sustained.

The lesson is that the frame counter measures `VSyncEvent` dispatches, not frames the guest
executed, and the two differ by a constant factor whenever the game uses `VSync(2)`.

### After the stage load

| | |
|---|---|
| guest memory | 300 distinct states over 150 s, still changing at the end |
| unmapped calls | 0 |
| crashed | false |
| overlays loaded | 3 |
| display | enabled, 320x240, NTSC |
| non-black pixels | **578 / 76800 (0.8%)** |
| distinct colours | 122 |

So the guest is alive and working, and the screen is essentially one flat colour with a
small element on it.

#### First, a correction about where the evidence comes from

An earlier run of this investigation read VRAM out of `Gpu.Vram`, the software shadow, and
reported a completely black display. That reading was **not trustworthy**.

`GpuHleForward` routes rasterisation to the GL backend as soon as `GpuHle.Active` and the
backend is ready. Once that is true the software shadow only ever receives CPU-to-VRAM
uploads and VRAM-to-VRAM copies; anything the guest **drew** exists only on the GL side. So
the shadow-based dump shows the uploaded textures with a permanently black framebuffer, which
reads exactly like "the game renders nothing".

`VramDump` now reads the GL backend when the HLE is active, handing the read to the GPU job
queue that the presentation loop drains, with a bounded wait so a stopped presentation loop
falls back to the shadow instead of hanging. **Every figure below is from the GL backend**,
and the dump reports which source it used so a future reader can tell the two apart.

#### What is actually in video memory

Mapping non-black content across VRAM gives a clear split:

| region | content |
|---|---|
| x 0-319, y 0-239 | black - this is the framebuffer |
| x 0-319, y 240-479 | black - this is the other framebuffer |
| x 320-639, y 0-255 | a uniform 192x256 block |
| x 320-1023, y 256-511 | substantial content |

Rendering VRAM `(320,256)` as 320x240 text produces a rich, high-frequency, unmistakably
photographic image - a pre-rendered room background. So **the background is in video memory,
at (320,256), as a texture.** It is not missing and it is not being decoded wrongly.

#### The display never looks at it

Sampling the display geometry over the whole run:

```
display geometries seen : 0,0 256x240 (disabled) | 0,0 320x240 | 0,240 320x240
```

The display origin is **only ever X=0**. It is never pointed at X=320. So the background at
(320,256) is a source texture that the game is expected to composite into a framebuffer at
(0,0) or (0,240) by drawing.

#### The composition produces nothing

Counting primitives through `RenderPrimEvent` separates the two screens sharply:

| | title screen | after stage1 |
|---|---|---|
| primitives | 193 | 494,028 - 581,553 |
| of which textured | **0** | 493,172 - 580,697 (99.9%) |
| vertices inside a framebuffer | 0 | 315,384 - 360,967 |
| in-framebuffer bounds | - | x 0..304, y 0..479 |
| vertices outside VRAM entirely | 0 | 131,217 |
| degenerate (all 3 vertices equal) | 0 | 20,890 |
| distinct CLUTs | 1 | 11 |

The in-framebuffer bounds of `x 0..304` are the giveaway: 304 + 16 = 320, so these are
**16x16 background tiles** covering the screen. About 263 of them per frame, which is what a
320x240 tiled background costs.

So the game is drawing textured background tiles at the correct screen positions, in large
numbers, and the result is 0.8% non-black.

**The single most useful fact here is the `0 textured` in the title column.** The port has
never once been observed rendering a textured polygon: on the title screen the background was
the uploaded image being displayed directly, which needs no rasteriser. Textured rendering is
first exercised at the stage load, and it does not work, so there is no working baseline to
compare against.

#### Ruled out

- **Texture page not tracked.** `GpuHleForward` derives the page from `_texPageX`/`_texPageY`
  and `GpuRaster` sets them from the texpage word, so the page is tracked and forwarded.
  `RenderPrimEvent.TexPage` being hardcoded to 0 in `GpuRaster.cs:118` is just an unpopulated
  event field, not evidence about sampling.
- **Wrong VRAM read path.** Corrected above; the dump is now GL-authoritative.

#### Ruled out: the drawing offset (a retraction)

**A previous revision of this document claimed the drawing offset was the root cause. That
claim was wrong and is retracted here, with the measurement that disproves it.**

The reasoning was that `HleDrawEnv` carries a clip rectangle but no draw offset, so a game
double-buffering by moving the drawing area would have its vertices clipped away. The premise
is true - `HleDrawEnv` really does have no offset field - but the conclusion does not follow,
because **the offset is already applied before the HLE sees anything.**

`GpuRaster` adds it while decoding the primitive:

```csharp
v[i].X = _drawOffsetX + CoordX(vw);      // GpuRaster.cs:53
v[i].Y = _drawOffsetY + CoordY(vw);      // GpuRaster.cs:54
```

and `GpuHleForward.HV()` then reads `v.Precise ? v.Px : v.X`, which is that already-offset
value. So vertices arrive in **VRAM coordinates**, and `HleDrawEnv.ClipX0..ClipY1` is in VRAM
coordinates too. The two spaces agree. `InterpBackend` applying the offset as well is
consistent with this, not evidence against it: it re-applies the offset because the frame graph
it replays holds pre-offset screen coordinates.

The measurement that settles it, added to `GpuActivity` for exactly this question:

```
vertices in screen space: 192689 (max vertex y < 240)
SCREEN vs VRAM clip     : 0        <- would be non-zero if the offset were missing
VRAM   vs VRAM clip     : 246028
draw-area tops          : 0:247156, 240:247446
```

`SCREEN vs VRAM clip` counts primitives whose vertices all sit in the second buffer's screen
space (y 0..239) while the clip rectangle is the second buffer (y 240..479) - the exact
signature the missing-offset theory predicts. It is **zero**. Instead 246,028 primitives have
VRAM-space vertices under a VRAM-space clip, which is correct. And the draw-area tops split
almost exactly 50/50, which is the double-buffer flip working as intended.

So the geometry the rasteriser receives is internally consistent, and every primitive that
should land in the displayed buffer is positioned to do so.

#### The decisive experiment: disable the HLE

`GpuHle.Active` is a settable static, so `--software-gpu` turns the HLE off for a run. The
software path is self-contained - `GpuCommands.CopyVramToVram` performs the shadow copy itself
and only *notifies* the HLE afterwards - so this isolates the HLE/GL layer cleanly.

Both VRAM stores are now dumped on every run, because each was separately believed to be the
truth and each produced a different black frame. Display crop, in each mode:

| mode | shadow store | backend store |
|---|---|---|
| HLE **on** (default) | 0 / 76800 (0.0%) | 584 / 76800 (0.8%) |
| HLE **off** (`--software-gpu`) | **69,444 / 76800 (90.4%)**, 1140 colours | 0 / 76800 (0.0%) |

Mapping content across both stores in both modes is what makes this conclusive:

```
MODE A (HLE on)  shadow   x0: 0, 0, 0, 3634        x320: 24576, 24576, 30962, 26633
MODE A (HLE on)  gl       x0: 578, 0, 581, 3634    x320: 24576, 24576, 30962, 26633
MODE B (HLE off) shadow   x0: 33604, 39396, 35168, 34354   x320: 24576, 24576, 30962, 26633
MODE B (HLE off) gl       0 everywhere
```

**The `x320` columns are byte-for-byte identical in all three populated stores.** Every texture
upload the game performs lands correctly, in both modes, in whichever store is in use. That
rules out the CD load path, the MDEC decode, the texture uploads, the draw-list decoding and
the guest code itself, in one comparison.

The **only** thing that differs is the framebuffer column `x0`, which is empty under the HLE and
full without it. So the defect is precisely: *under the HLE, the content destined for the
framebuffer never arrives.*

#### Why, and why disabling the HLE fixes it

The mechanism, from the code:

- `HleLoadBegin` sets `_hleLoadActive = HleOn`. So a CPU-to-VRAM upload is buffered and then
  pushed to the backend with `WriteVram` **only when the HLE is on**.
- `InterpBackend` is the active backend, and when `_active` is true it **defers**: `WriteVram`
  and `DrawTri` are appended to a frame graph (`_recording`) instead of being forwarded to the
  inner `GlCore`, and are applied later by `Replay`.
- With the HLE off, `_hleLoadActive` is false, so `HleLoadFlush` returns immediately and the
  upload is never deferred. It reaches the shadow directly through
  `GpuCommands.StoreImageHalfword`, which writes `Vram[idx]` unconditionally, and nothing
  afterwards displaces it.
- With the HLE on, the deferred copy is what should reach the framebuffer, and the shadow is
  additionally overwritten by `HleReadback`, which writes backend contents back into the shadow
  (`GpuHleForward.cs:115-120`). If the backend's framebuffer is empty, that readback writes
  emptiness into the shadow - which is exactly the `x0: 0, 0, 0` in mode A.

So the fault is in the **deferred recording layer** (`InterpBackend`'s graph) or in what
`GlCore` does with the replayed writes, and disabling the HLE sidesteps both. That is why
turning it off produces a complete, correct VRAM image.

#### What this establishes, and what it does not

Established: the guest code, disc reads, MDEC decoding, texture uploads and draw-list decoding
all work. A complete and correct VRAM image exists with the HLE off - 90.4% non-black, 1140
distinct colours, and a coherent scene in
`out/diagnostics/framebuffer-shadow-ascii.txt`.

Not established: `--software-gpu` does **not** by itself put a picture on screen. The backend
store is empty in that mode, and presentation reads the backend, so the window still shows
nothing. It is a diagnostic, not a workaround, and it must not be described as making the game
playable.

#### A timeline of both stores, and what it adds

A single end-of-run dump cannot distinguish "never filled" from "filled and then overwritten".
Sampling non-black pixels in the display region over time, for both stores, does:

```
   0,0s  shadow      0   backend      -
   3,0s  shadow  31801   backend      -      <- title screen appears in the shadow
   4,0s  shadow  31801   backend  31906      <- both stores AGREE
   8,0s  shadow  31801   backend  13203      <- backend loses content the shadow keeps
   9,0s  shadow  61135   backend      -
  12,0s  shadow  61135   backend  21840
  16,0s  shadow  61135   backend  12104
  19,0s  shadow      0   backend      -      <- both collapse, at the stage transition
  20,0s  shadow      0   backend    581
  28,0s  shadow      0   backend    586
```

Three things follow.

**The pipeline can work.** At 4 s the two stores agree to within 0.3% (31801 vs 31906). Whatever
is wrong is not a permanently broken path - it works, and then it stops working.

**The backend loses content the shadow retains.** Between 4 s and 8 s the backend drops from
31906 to 13203 while the shadow holds at 31801. Content *disappearing* from the backend, rather
than never arriving, points at something overwriting it: `GlCore` flushes dirty render targets
into `_vram` with `WritebackDirtyIntersecting`, so a target that holds only what was *drawn*
(a cleared surface plus a handful of primitives) would be written over the uploaded background
and take two thirds of it away. That is the shape of the symptom.

**Both stores collapse at the stage transition**, around 19 s, and neither recovers: the backend
settles at ~580 pixels, which is the small text element, and the shadow at zero. So the room
background never arrives after the transition, and a single dump taken at the end shows only
that final state - which is why it read as a uniformly black screen for so long.

#### Two mechanisms ruled out by reading the code

Both candidates from the previous round are disproven, which is worth recording so they are not
retried.

**Ordering within a frame is correct.** `InterpBackend.Replay` walks the recorded operations by
index and dispatches them in that order:

```csharp
for (var i = 0; i < ops.Length; i++)
{
    var slot = slots[i];
    switch (ops[i]) { case GraphOp.DrawEnv: ... case GraphOp.WriteVram: ... }
}
```

and `Settle()` replays `_current` before a read, so an upload recorded earlier in the frame is
applied before anything that could observe it. The hazard of a flush racing a pending upload was
imagined, not real.

**Render targets are seeded, not created empty.** `GetOrCreateRt` ends with

```csharp
fresh.Create(_gl);
_rts[slot] = fresh;
SyncRtFromVram(fresh, fbX, fbY, fbW, fbH);     // GlCore.cs:304
```

so a newly created target is filled from `_vram` before it is drawn into, and a reused one is
re-seeded by `SyncRtsFromVram` whenever an upload or VRAM copy arrives. A flush therefore writes
back upload-plus-drawing, not an empty surface.

#### The culprit: textured drawing

Suppressing classes of primitive through `RenderPrimEvent.Skip`, which `GpuRaster` honours
before consuming the vertices, splits the problem cleanly:

| suppressed | shadow store | backend store | primitives skipped |
|---|---|---|---|
| **textured** | **0 / 76800 (0.0%)** | **0 / 76800 (0.0%)** | 284,803 |
| **flat** | 67,603 / 76800 (88.0%) | 67,560 / 76800 (88.0%) | 856 |
| all | 61,135 / 76800 (79.6%) | 61,135 / 76800 (79.6%) | 285,659 |

Three conclusions, and they are strong ones.

**The upload, VRAM-copy and fill paths are correct under the HLE.** With every draw suppressed
the two stores agree *exactly* - 61135 against 61135, and 1182 colours - and the frame is a
coherent room. So CD loads, MDEC decoding, CPU-to-VRAM uploads, VRAM copies and fills all work.

**Textured primitives are what destroys the frame.** Suppress them and everything goes black,
including content that was correct a moment earlier. They cover the screen - about 263 of them
per frame at 16x16 - so a textured draw producing black produces a black screen.

**Flat primitives are legitimate.** Suppressing them leaves the frame *better* (88.0% versus
79.6%), which means they add content rather than remove it. A fade or clear quad would have
shown the opposite.

So the remaining defect is one thing: **a textured primitive renders black.** Geometry is proven
correct, the texture data is proven present in both stores, and the draw reaches the backend. What
is left is the sampling - which texture page and CLUT the rasteriser resolves for those tiles.

The texture page is the leading suspect. The room background sits at VRAM x = 320, which is page
X = 5. Most games keep textures in the low pages, so a page-resolution defect would not have been
exercised by other games, and sampling page X = 0 - the region this game's framebuffer occupies -
would return black on exactly these tiles. Worth noting for whoever picks this up:
`RenderPrimEvent.TexPage` is hardcoded to 0 in `GpuRaster.cs:118`, so that field cannot be used to
observe it; the value has to come from `Gpu.CurTPage()` on the runtime side or from the shader.

#### Next probe

Confirm the texture page and CLUT the rasteriser resolves for a background tile against where the
data actually lives. `GpuActivity` already reports the distinct CLUTs seen (11). If the resolved
page is 0 while the data is at page 5, that is the defect, and it is a small one to fix - either
the texpage is not reaching the backend, or the shader decodes it differently from
`Gpu.CurTPage()`.

---

## Evidence

### The recompiled guest runs

`dotnet run --project port/RE15pc -- --smoke 25` produces:

```
[DiscIdentity] accepted ...\Bio2Nov96.cue
[Gpu] context: 4.5 (4.5.0 NVIDIA 617.14) on NVIDIA GeForce RTX 3070/PCIe/SSE2
[Gpu] backend: Gl45
[assets] game=PSXEXE packs=0 xa=0 tex=0 cluts=0 rules=0 root=...\packs
[Dispatcher] loaded overlay: main
ResetGraph:jtb=8007e308,env=8007e350
[Dispatcher] loaded overlay: title
```

`ResetGraph:jtb=...` is not printed by the runtime - it is the guest's own debug output,
reached through the BIOS printf implementation. So `Dispatcher.Call(c, m, 0x80054448u)`
returned into real game code and that code ran far enough to configure the GPU and load
an overlay.

Run report: `crashed=False`, `unmapped calls=0`, `overlays loaded=2`, `overlay evictions=0`,
`region overwrites=0`, `vram collisions=0`, `disc read warnings=0`, `listener errors=0`.
`verdict.txt` says `PASS`.

### The guest is progressing, not wedged

Recompiled code has no program counter to sample - control flow is the C# call stack and
`CpuContext` carries only the GPRs plus SR/EPC/BadVAddr. So progress is measured by
hashing guest memory on a timer: a wedged guest stops writing RAM and the hash goes
constant, a running guest keeps changing it.

```
guest memory states seen : 48 distinct
last change at          : 25,0s
seconds since change    : 0,0
verdict                 : PROGRESSING
```

48 distinct memory states across a 25 second window with the last change at the moment of
sampling. The guest is alive and mutating state.

With `--log irq,vsync,cd,sdk,bios` the runtime reports **2840 interrupt deliveries in 20
seconds**, and the guest's own calls are visible: `[SDK] VSync(-1)`, `[SDK] VSync(2)`,
`[IRQ] dma ch 6 dicr = 0x00900000`, `[BIOS] B(16) PAD_dr`, `[BIOS] B(10) ChangeTh`.

`ChangeTh` plus the pad reads plus DMA channel 6 is the engine's threaded main loop
running: threads, controller polling, and GPU DMA.

### The title screen is on screen

```
display enabled         : True
display area            : 320x240 at VRAM (0,240), NTSC
non-black pixels        : 31801/76800 (41,4%)
distinct colours        : 1123
```

A blank or noisy framebuffer is excluded by those two numbers together: 41% of pixels are
something other than black, and they use only 1123 distinct colours, which is a picture
rather than garbage.

`out/diagnostics/framebuffer-ascii.txt` renders the display area as luminance text, because
a PNG needs an image-capable reader and text can be checked from a log:

```
                                 ..          ...  ..  ... .
             ::....  ... ......  ... ...  ::::.  ......  ....   ......  ......      ......
            .::.:::  ... ....... ... .::  ::::.   .....  ....   ....... .......    ........
             ::..::  ... ... ... ... .::  ::::.     ...  ....   ... ... ... ...    ...  ...
                    ...
                     :-        .::--=++*#%%##*+=+     +===---::..            ....
                    .            .:-=+++++++====+     =-----:::...             ..
                    .            .:-==============     =--::::....   .        .  .
                                  .:--=======----=     -:::::....    .      . ..
                                   .:::-----------    .::::.....    :           ..
                                     ..:::::::::::    :......  .:.:     .......  .
```

### Which image it is, proven by byte comparison

Four files on the disc are 153,620 bytes: `C_BACK2.TIM`, `SELECTH.TIM`, `TITLEJ.TIM`,
`TITLEU.TIM`. All four are 16-bit 320x240 TIMs (flags `0x00000002`, no CLUT), declaring VRAM
position (0,0), with a 20-byte header followed by 320x240x2 = 153,600 bytes of pixels.

Comparison against `out/diagnostics/vram.rgb555.bin`, mapped the way a width-320
`LoadImage` actually places data (`source word s -> VRAM word (s % 320, 240 + s / 320)`):

| TIM | match against VRAM at display origin |
|---|---|
| `TITLEJ.TIM` | **76800/76800 = 100.00%** |
| `TITLEU.TIM` | 59908/76800 = 78.01% |
| `C_BACK2.TIM` | 21639/76800 = 28.18% |
| `SELECTH.TIM` | 8679/76800 = 11.30% |

An exact, complete, word-for-word match for `TITLEJ.TIM`. The Japanese title image is the
one on screen.

The `TITLEU.TIM` figure is worth noting as a trap: 78% looks like a near-match but is what
you get from two largely black images compared against each other. Only the 100.00% result
is a match, and the discriminating evidence is that it is exact rather than merely high.

The same comparison against the `(0,0)` half of VRAM also gives 100%, so the identical
image was uploaded twice, at `(0,0)` and at `(0,240)`.

---

## Defect: the title image is displaced 10 pixels horizontally

The matching rule is the finding. The test that returns 100% compares display word `d`
against the **file's** word `d` - that is, against `TITLEJ.TIM` from byte 0, header
included:

```
VRAM (0,0)    : 10 00 00 00 02 00 00 00 0C 58 02 00 00 00 00 00
VRAM (0,240)  : 10 00 00 00 02 00 00 00 0C 58 02 00 00 00 00 00
```

`10 00 00 00` is the TIM id byte and version, `02 00 00 00` the flags, `0C 58 02 00` the
image block size `0x0002580C` = 153,612. That is a TIM header sitting in video memory at
the display origin.

So the pixels are not where they belong. The image's first pixel should be at display
`(0, 240)`; instead it is at `(10, 240)`, because the ten 16-bit words of the file header
were uploaded first and consumed ten pixels of the first row. The whole picture is
consequently displaced **10 pixels to the left**, wrapping at each row boundary.

This is consistent with the luminance render above, which shows a coherent picture rather
than the mess a grossly misplaced upload would produce: a 10 pixel shift on a mostly dark
title screen is subtle, which is exactly why it took a byte comparison to find.

### How far this is narrowed

Four things are now established, which between them remove most of the search space.

**1. RAM holds the file correctly.** Dumping all of guest memory and searching it for the
TIM header finds it in exactly one place:

```
RAM offset 0x198000  (guest 0x80198000)
  0x80198000 : 10 00 00 00 02 00 00 00 0C 58 02 00 00 00 00 00 40 01 F0 00
  0x80198014 : 00 80 00 80 00 80 00 80 ...            <- pixel data, at exactly +20
```

So the file sits at a clean, aligned base with the header at offset 0 and the pixels at
offset `0x14`. A loader that wanted the pixels would pass `0x80198014`. The file is not
misplaced in memory, and it is not present anywhere else.

**2. The runtime's transfer is faithful.** `GpuCommands.BeginImageLoad` takes the
destination from the command word and the size from the next, and
`StoreImageHalfword` places each following word at
`(loadX + px % w, loadY + px / w)`. It writes exactly the words the guest sends, in order.
There is no offset for it to lose.

**3. The guest does not build a command buffer in RAM.** Searching memory for the
`LoadImage` size word `0x00F00140` finds 16 occurrences and none of them is a GPU command
stream; they are sprite coordinate pairs and pointer tables. So the pixel data is reaching
the GPU some other way, most plausibly DMA reading directly out of the TIM buffer.

**4. Therefore the data stream began at `0x80198000`, not `0x80198014`.**

What is still not settled is whether that is the guest's own choice or a guest-visible
difference introduced by the port. Both remain possible:

- the game feeds the buffer base and real hardware shows the same 10-pixel displacement, in
  which case this build simply has this defect and the port is reproducing it faithfully;
- the game computes `base + 20` and something in the port changes that arithmetic, in which
  case it is a real port bug affecting every TIM upload, not just this one.

Deciding between them needs the guest's TIM loader located and read. Useful starting points:
`generated/title.cs` references `0x8019xxxx` at guest addresses `0x801010C0`, `0x801010CC`,
`0x801010D4`, `0x80101164`, `0x80101194`, `0x801011C0`, `0x80101FE4` and `0x80102040`, and no
literal `0x80198014` appears anywhere in the generated code, so if the `+20` exists it is
computed rather than folded into a constant.

**Do not "fix" this by patching VRAM.** Establish which side is wrong first. A symptom patch
here would hide the same defect on every other TIM in the game.

---

## Reproducing

```powershell
# guest runs, framebuffer dumped, verdict written to out/diagnostics/verdict.txt
dotnet run --project port/RE15pc -- --smoke 25

# with the runtime's own logging, to watch interrupts and SDK calls
dotnet run --project port/RE15pc -- --smoke 20 --log irq,vsync,cd,sdk,bios
```

Artifacts written to `out/diagnostics/`: `console.log`, `report.txt`, `verdict.txt`,
`framebuffer.png`, `vram-full.png`, `vram.rgb555.bin`, `framebuffer-ascii.txt`,
`ram-overlay-80100000.bin` (the overlay region) and `ram-full.bin` (all 2 MB of guest RAM,
which is what makes it possible to search memory for a known byte signature).

### A note on the process exit code

The smoke harness terminates the process with `TerminateProcess` rather than
`Environment.Exit`. `Environment.Exit` unwinds finalizers on a thread-pool thread while the
guest thread and the native audio, video and detour libraries are live, and produces
`0xC0000409`; calling `Runtime.Shutdown()` from the same thread produces `0xC0000409`'s
cousin `0xC0000005`, faulting in `GL.DeleteFramebuffers` with no current GL context. Since
the guest main loop never returns on its own, the harness must stop the process from
outside, and terminating outright is the only path that leaves the exit code meaningful.
`verdict.txt` is authoritative regardless.

---

## Next

1. **Verify the render-target ordering.** The mechanism is in the section above: `Writeback`
   blits the whole target surface into `_vram`, targets are seeded only when an upload reaches
   `GlCore`, and `InterpBackend` defers uploads into a frame graph. If a flush can be issued
   between a recorded upload and its replay, uploaded VRAM contents are overwritten with
   emptiness - which is what the timeline shows. Confirm the ordering, then seed targets from
   the pending graph rather than from `_vram`.
2. **Turn reaching STAGE1 into a one-line regression check.** It currently needs a hand-written
   `--input` script; naming a standard script for it would make every later phase cheap to
   re-verify.
3. Settle the owner of the 10-pixel displacement. Narrowed as far as static reading can take
   it; see the section above for where to look.
4. Audio. Nothing has been proven to play at all.
5. Recompile twice and confirm `generated/` is byte-identical, which is what decides whether
   it may stay ignored.
