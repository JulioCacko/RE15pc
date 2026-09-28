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

The immediate blocker is identified at root cause: the composed frame comes out **black**
because the HLE draw path cannot receive the PlayStation's drawing offset and therefore clips
every primitive away. The room background is provably present in video memory at (320,256)
as a 320x240 pre-rendered image, the display is at (0,0) or (0,240), and about 263 textured
background tiles per frame are drawn into the framebuffer at the right screen positions and
produce almost nothing. The mechanism, the evidence and the shape of the fix are below.

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

#### Root cause

**The HLE draw path cannot receive the PlayStation's drawing offset, so it clips every
primitive away.**

Sampling the draw offset across the run:

```
draw offsets seen       : 0,0 | 0,240
drawing area(s) seen    : 0..1023 x 0..1023 | 0..319 x 0..239 | 0..319 x 240..479
```

The game double-buffers by moving the drawing area, exactly as expected: one buffer is
`(0,0)..(319,239)` with draw offset `(0,0)`, the other is `(0,240)..(319,479)` with draw
offset `(0,240)`. On real hardware a vertex at screen `(0,0)` is rasterised at
`vertex + drawOffset`, so with offset `(0,240)` it lands at VRAM `(0,240)` - inside the clip
area.

The HLE draw path forwards the clip rectangle but not the offset. `HleDrawEnv` is:

```csharp
public struct HleDrawEnv
{
    public int ClipX0, ClipY0, ClipX1, ClipY1;
    public int TwMaskX, TwMaskY, TwOffX, TwOffY;
    public bool SetMask, CheckMask, Dither;
}
```

There is no offset field, and neither `HleVertex` nor `HleRect` carries one either, so there
is nowhere for `Gpu.DrawOffsetX`/`DrawOffsetY` to go. `GpuHleForward.CurEnv()` builds the
draw environment from `_drawAreaLeft/Right/Top/Bottom` and the texture-window, mask and
dither state - and stops there.

The consequence is deterministic. For the second buffer the backend is handed
`ClipY0 = 240, ClipY1 = 479` and vertices at screen y `0..239`. Every tile lies entirely
outside the clip rectangle, so every tile is discarded and the framebuffer keeps whatever it
was cleared to. **That is the black screen.**

It also explains why the title screen worked: its background is an uploaded image being
displayed directly, which needs no rasteriser at all. Textured rendering is first exercised
at the stage load, and this defect is only reachable once a game uses a nonzero draw offset,
which is why it had not been seen before.

#### The fix, and why it is a patch

The offset has to be forwarded and applied inside the backend, so this cannot be worked
around from the host. That makes it the first genuine need for an entry in `patches/`,
against the pinned upstream revision, exactly as `docs/compatibility.md` anticipated. Per the
upstream README's stance on contributions, it stays a local patch rather than a pull request.

The shape of the change:

1. Add `OffsetX`/`OffsetY` to `HleDrawEnv`.
2. Populate them in `GpuHleForward.CurEnv()` from `_drawOffsetX`/`_drawOffsetY`.
3. Apply them where each backend rasterises - the same place `ClipX0..ClipY1` is consumed -
   so that a vertex at screen `(0,0)` lands at `offset + (0,0)` in VRAM space, matching the
   clip rectangle's coordinate space.

Verification is already in place, which is what makes this safe to attempt: run
`--smoke 45 --input <script>`, and the framebuffer crop should stop being 0.8% non-black and
start showing the room background that is provably sitting at VRAM `(320,256)`. The title
screen is the control - it needs no rasteriser, so if it regresses the patch is wrong.

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

1. **Apply the draw-offset patch** described above, then confirm the framebuffer stops being
   0.8% non-black. This is the blocker; everything else waits on it.
2. **Turn reaching STAGE1 into a one-line regression check.** It currently needs a hand-written
   `--input` script; naming a standard script for it would make every later phase cheap to
   re-verify.
3. Settle the owner of the 10-pixel displacement. Narrowed as far as static reading can take
   it; see the section above for where to look.
4. Audio. Nothing has been proven to play at all.
5. Recompile twice and confirm `generated/` is byte-identical, which is what decides whether
   it may stay ignored.
