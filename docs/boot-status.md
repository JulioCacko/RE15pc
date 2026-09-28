# Boot status

Where the port actually is, what has been proven, and what is known to be wrong.
Everything below is backed by an artifact on disk or a command that can be re-run.

Last updated at the end of the rendering investigation.

> **This document is chronological, and later sections supersede earlier ones.** It is kept in
> order deliberately, because the record of what was proposed and then disproved is more useful
> than a tidied conclusion. The summary below is current; anything below it that contradicts the
> summary has been withdrawn, and each withdrawal says which measurement killed it.

---

## Summary

The recompiled prototype **boots, runs, and plays through its opening screens into STAGE1**.
Guest code executes, receives interrupts, decodes images through MDEC, loads the `title` overlay
and is driven by scripted controller input past the title and character select until the `stage1`
overlay loads. There are **zero unmapped calls**.

**Verified and gated.**
- Disc identity: hash and file layout, by `tools/New-DiscManifest.ps1 -Check`.
- Recompiler determinism: two runs are byte-identical across all 10 files, by
  `tools/Test-RecompileDeterminism.ps1`, which fails correctly when a generated file is perturbed.
- Overlay dispatch: **all seven** overlays dispatch correctly, by `--verify-overlays` (8 of 8,
  0 failed), through the real LBA mapping and base-write promotion rather than by gameplay.
- Audio: the SPU **produces output** - peak 8619 of 32767 from a block mixed while voices carried
  volume - by `--verify-audio`.
- Title screen: `TITLEJ.TIM` resident at the display origin at a **100.00% word-exact** match.
- Guest progress: 30 fps, 2840 IRQs in 20 seconds, progressing rather than wedging.

**The blocker is resolved, and the earlier reading of it was wrong.** This section previously
described the composed frame as black and the room's drawn output as never reaching video memory.
Neither was true. The room renders at **79.6% of the framebuffer, in both buffers**, and the game
then fades itself out by drawing flat **subtract** rectangles over that correctly rendered image -
which is what a fade to black is. `--skip-draws flat` removes those rectangles, so every conclusion
drawn from it compared a faded frame against an unfaded one rather than a broken frame against a
working one.

**The blocker that was real is also resolved.** The guest used to stop at frame 704 with an unmapped
call at `0x800100AC`: an indexed dispatch table whose entries the function detector had merged into
their neighbouring functions, so the guest jumping into the middle of `func_8001003C` found nothing
to dispatch to. **33 computed-jump targets** were added to `port/config/funcmaps/main.json` -
deliberate curation of a generated file, since `--autoconfigure` would discard it - and the same
sustained input now runs **5414 guest frames with zero unmapped calls**.

**What remains is play.** The game executes the opening sequence correctly and directions change
nothing in it: 87 framebuffer generations with directions held against 86 without, frame counts
within 1.3%. No room has been walked, no item taken, no door opened, no save made.

**Mechanisms proposed here and withdrawn after measurement.** Each of these is written up below
with the evidence that killed it, and none should be re-tried without new data:
the drawing offset being missing, then being applied twice; the HLE's deferred recording layer;
the deferred frame graph's ordering; render targets being created without seeding; the drawing
offset in `InterpBackend`; the mask bits; the subtract blend; the flat blend-0 subset; a stuck
fade overlay; render target eviction; `FillRect` and `CopyVram` as the wipe; the texture page
being wrong; the dark modulation colour; vertex batch target coherence; and OpenGL errors.

Two failure modes have recurred and are worth stating, because they account for most of the
withdrawn mechanisms. **Reasoning from what code intends rather than from the values it actually
has**, and **sampling at one moment in time** - the latter produced both the "one-off erasure"
reading and the "silent audio" verdict, each corrected only by changing *when* the sample was
taken.

One rendering defect is also identified and quantified: before the stage load, the title
image was uploaded to VRAM **including its 20-byte TIM header**, displacing the picture
10 pixels horizontally. Root cause not yet attributed.

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

#### The room renders: `--skip-draws flat`

**A previous revision of this document read the table below backwards and concluded that textured
primitives destroy the frame. It is the opposite, and the correction matters because it means the
port already renders this game.**

Read the table for what is *skipped*, not for what is drawn:

| suppressed | what still draws | display region | primitives skipped |
|---|---|---|---|
| **textured** | flat only | **0 / 76800 (0.0%)** | 284,803 |
| **flat** | textured only | **67,603 / 76800 (88.0%)**, 1356 colours | 856 |
| all | nothing | 61,135 / 76800 (79.6%) | 285,659 |

So **textured drawing produces the room correctly** - 88.0% non-black, and both stores agree
(67,603 against 67,560) - while the **flat primitives are what paint it black.** With everything
suppressed the uploaded background alone accounts for 79.6%, textured tiles add detail to reach
88.0%, and the flat primitives take it to zero.

That is a strong result to have arrived at sideways: `--skip-draws flat` renders the game.

#### What the flat primitives are

```
flat primitives (all)   : 856 (632 semi-transparent, 360 spanning >= 300x200), bounds x 0..1023, y 0..1263
flat blend modes        : 0:224, 2:632   (0=avg 1=add 2=sub 3=add/4)
flat mask state         : -/-:856
textured under CheckMask: 0
```

Measured across all of them, not only the ones inside the framebuffer, because that subset was
actively misleading: 108 of the 856 are in-frame, and the test for it requires every vertex to be
inside `0..319 x 0..479`, so a primitive that covers the screen fails it by exactly one pixel. The
in-frame subset is the wrong place to look, and looking there first cost a round.

Three useful facts follow.

**There are 360 screen-covering flat primitives.** Three hundred of them spanning at least
300x200, over roughly 780 frames, is around one every other frame. Bounds reach `x 0..1023,
y 0..1263`, so some extend past the VRAM edge entirely.

**They use two blend modes, not one.** 632 are semi-transparent with blend mode 2 (subtract) and
224 use blend mode 0 (average, 50/50 with the destination).

**The mask bits are not involved.** Every one of the 856 is `-/-`, neither setting nor checking the
mask, and no textured primitive is ever drawn under CheckMask. That explanation is out.

#### The draw order is correct, which removes the fade explanation

The obvious reading of a screen-covering black quad is a fade overlay drawn last and never
completing. That is now measured and it is wrong:

```
screen-covering flat quads: 360, max textured draws after one: 143772
```

**143,772 textured draws follow a screen-covering flat quad.** The quads are drawn early, as clears,
and the scene is drawn over them afterwards, in the intended order. So there is no stuck fade, and
the quad is not an overlay at all.

That also resolves the apparent contradiction in the previous section, and the resolution is worth
stating because it made two earlier predictions look falsified when they were only incomplete. The
360 screen-covering quads are split across both blend subsets - 632 of the flat primitives are blend
2 and 224 are blend 0, and screen-covering ones exist in both. So:

- suppressing the blend-2 flat primitives leaves the blend-0 screen-covering quads,
- suppressing the blend-0 flat primitives leaves the blend-2 screen-covering quads,
- and either way a screen-covering flat quad is still drawn, so the frame stays black.

Only suppressing **all** flat primitives removes every screen-covering quad at once, and only that
reproduces the fix. The sub-class predictions failed because they each left half the culprits in
place, not because the quads are innocent.

#### What this pins down

Putting the two facts together: a screen-covering flat quad is drawn first, 143,772 textured draws
follow it at correct positions sampling correct data, and the frame still comes out black - unless
no screen-covering flat quad is drawn at all, in which case the scene appears at 88%.

**So drawing a screen-covering flat quad breaks the backend state for everything drawn afterwards.**
It is not that the quad covers the scene; it is that after the quad, the scene's own draws stop
producing pixels. That is a state defect, it is independent of blend mode, and it is triggered by a
primitive large enough to cover the framebuffer.

The candidates are all in how a draw picks and binds its render target, because that is the machinery
a large primitive exercises differently from a small one:

- `GlCore.Classify()` selects a target from the display-rect ring and caches on `_env.ClipX0..ClipY1`.
  `ClassifySlow` needs the clip to be inside a registered rect, or to be a registered rect within
  `FbSlackW`/`FbSlackH` (64 and 32) of it.
- The blend-2 path calls `_vram.BeginDestRead(...)` and then `RebindTarget(rt)` mid-draw to read the
  destination. A rebind that is not fully undone would leave subsequent draws bound to the wrong
  framebuffer, which would make them land somewhere invisible - exactly the symptom.
- `GlCore.FillRect` walks the render targets and can take a `FillRtFull` path, which a large quad
  makes more likely to be selected.

#### The draws do land: it is not a classification or binding defect

`Classify` is private, but its effect is observable. `VramTracker` is public and `GlCore` calls
`MarkGpuWrite` when it writes a render target back into VRAM and when it draws without one, so
sampling `IsGpuDirty` and `Generation` for each framebuffer region says whether draws are reaching
it:

| configuration | framebuffer (0,0) | framebuffer (0,240) | display crop |
|---|---|---|---|
| default | gpu-dirty 50/52 samples, generation changed 50x | gpu-dirty 50/52, changed 50x | 585 (0.8%) |
| `--skip-draws flat` | gpu-dirty 43/51, changed 46x | gpu-dirty 43/51, changed 46x | 67,557 (88.0%) |

**Both regions are written on essentially every sample in both configurations.** The draws are
landing in the framebuffer. So the three candidates the previous section listed - `Classify`
selecting a target that is never composited, `RebindTarget` leaving the wrong framebuffer bound, and
`FillRect`'s `FillRtFull` path - are all eliminated, because a mis-bound or uncomposited target would
show as a region that stops being marked written. It does not.

That leaves a narrower and more awkward statement. In the default configuration the textured draws
happen after the clear, land in the framebuffer, sample correct data, sit at correct positions - and
the pixels they produce are black. Removing the screen-covering flat quads changes the *colour* the
same draws produce.

#### The remaining candidate: blend state left by the clear

A primitive before them changing the colour they produce points at blend state rather than geometry,
and `GlCore`'s semi-transparent path is the place to look:

```csharp
if (_legacy)              { Disable(Blend); DrawArrays(...); }
else if (!_kTransparent)  { Disable(Blend); DrawArrays(...); }   // no BlendEquation reset
else
{
    Enable(Blend);
    BlendFuncSeparate(Src1Color, Src1Alpha, One, Zero);
    if (_kBlend == 2)
    {
        BlendEquation(FuncAdd); SetBlend(0f, 1f); DrawArrays(...);
        if (needDest) { _vram.BeginDestRead(destTex, ...); RebindTarget(rt); }
        BlendEquationSeparate(FuncReverseSubtract, FuncAdd); SetBlend(1f, 1f);
        Uniform4(_uBlendOpaque, 0f, 0f, 0f, 1f); DrawArrays(...);
    }
    ...
}
```

Two things stand out. First, the opaque branches **disable blending without resetting the blend
equation**, so `FuncReverseSubtract` set by an earlier subtract-blended quad stays in the GL state.
Second, and more significantly, the shader is told the blend mode as a uniform
(`Uniform1(_uBlendMode, _kBlend)`, line 861), so the blend may be implemented *in the shader* against
a destination-read texture rather than by fixed-function blending.

A reverse subtract evaluated as `destination - source` against a black clear gives `0 - source`,
which clamps to black - and that is precisely the observed result: correct geometry, correct texture,
landing in the right place, producing nothing. It would also explain why removing the subtract-blended
screen quads changes the colour of draws that come after them.

#### Both blend paths are internally consistent (two candidates retracted)

Two asymmetries looked wrong on first reading and both turn out to be fine, which is worth
recording so they are not "fixed".

There are **two fragment shader variants**, and each set of uniforms belongs to its own:

| variant | colour output | blending |
|---|---|---|
| `PrimFs` (core, GLSL 330+) | `layout(location = 0, index = 0) out vec4 FragColor;` and `layout(location = 0, index = 1) out vec4 BlendColor;` | fixed-function, dual-source |
| `PrimFs120` (GLSL 120) | `gl_FragColor` | in-shader, via `uSemiTrans` and `uBlendMode` |

So `Src1Color`/`Src1Alpha` **are** meaningful on the core path, because `PrimFs` really does write a
second colour output at index 1 - that retracts the dual-source suspicion. And `uSemiTrans` and
`uBlendMode` being assigned only inside `if (_legacy)` is correct rather than a defect, because they
belong to `PrimFs120`, which is the shader that path uses - that retracts the legacy-only suspicion.
`_uBlendOpaque` belongs to `PrimFs`, which is why the non-legacy branch sets it and the legacy one
does not.

#### Retracted: subtract-blended draws are not no-ops

The previous revision claimed that a subtract-blended primitive draws nothing when `needDest` is
false. That is wrong, and the derivation that corrects it also shows the two-pass path is right.

`needDest` is not a per-primitive property at all:

```csharp
var needDest = _legacy || _kCheckMask != 0;      // GlCore.cs:803
```

On core GL with the mask bits never set - both measured, `_legacy` is false for a GL 4.5 context and
every flat primitive reports `-/-` - `needDest` is correctly false for every draw. The earlier claim
assumed pass 2 needed the destination bound as a texture, and it does not.

Re-deriving the two passes with `needDest` false, and recalling that `BlendColor.rgb` is the source
factor and `BlendColor.a` the destination factor, because the shader picks between `uBlend` and
`uBlendOpaque` per texel:

- **Pass 1**, `SetBlend(0f, 1f)` with `uBlendOpaque = (1,1,1,0)`: an *opaque* texel takes
  `uBlendOpaque`, giving source factor 1 and destination factor 0, so it is **written normally**;
  a semi-transparent texel takes `uBlend`, giving source 0 and destination 1, so it is left alone.
- **Pass 2**, `FuncReverseSubtract` with `SetBlend(1f, 1f)` and `uBlendOpaque = (0,0,0,1)`: the
  opaque texel is left alone, having already been written; the semi-transparent texel computes
  `destination x 1 - source x 1`, which is the subtraction.

Fixed-function blending reads the destination **implicitly**, so no destination copy is required.
`BeginDestRead` exists for the in-shader blend that `PrimFs120` performs, which is why `needDest`
is true on the legacy path - the two are consistent, and the check is not a defect. The two-pass
design is correct, and the claim that subtract draws are no-ops is withdrawn.

That is the third mechanism in this document to be retracted after being written up as a finding.
The pattern is consistent: each was derived by following the code's *intent* rather than reading what
the code actually does with the values it has.

#### First observed GL state, rather than inferred

`GlStateSampler` reads the live GL state through `GpuGlAccess.Gl` on the GPU job queue, every eighth
progress tick. Six samples per run, which is a small number and is treated as such below.

The black run and the correct run differ:

```
DEFAULT (black), 6 samples, 4 distinct states
  x3  blend=True  eqRgb=FUNC_REVERSE_SUBTRACT  srcRgb=0x88F9 dstRgb=SRC1_COLOR srcA=ONE dstA=ZERO  fbo=1 origin=(0,240) margin=0 target=1280x960
  x1  blend=False eqRgb=FUNC_REVERSE_SUBTRACT  ...  origin=(0,240)
  x1  blend=True  eqRgb=FUNC_REVERSE_SUBTRACT  ...  origin=(0,0)
  x1  blend=False eqRgb=FUNC_ADD               ...  origin=(0,0)

--skip-draws flat (correct), 6 samples, 4 distinct states
  x2  blend=False eqRgb=FUNC_ADD  srcRgb=0x88F9 dstRgb=SRC1_COLOR srcA=ONE dstA=ZERO  origin=(0,240)
  x2  blend=True  eqRgb=FUNC_REVERSE_SUBTRACT  ...  origin=(0,0)
  x1  blend=False eqRgb=FUNC_ADD  srcRgb=ONE    dstRgb=ZERO      srcA=ONE dstA=ZERO  origin=(0,240)
  x1  blend=False eqRgb=FUNC_ADD  srcRgb=ONE    dstRgb=ZERO      srcA=ONE dstA=ZERO  origin=(0,0)
```

Four things are established by this and are worth separating from what is only suggested.

**The render target alternates between both buffers, and `Classify` is working.** Origin appears as
both `(0,240)` and `(0,0)` in both runs. `margin=0` throughout, so no widescreen bias is in play.

**`fbo=1` on every sample.** The bound draw framebuffer is the same object in all six samples of both
runs even though the target origin alternates. Either the two buffers share one framebuffer object
with the origin supplied as a uniform, or `Classify` reuses one target - the latter would be worth
knowing, given the two buffers are supposed to be independent.

**The blend factors are the dual-source ones** from `GlCore.cs:887` - `dstRgb` reads as `SRC1_COLOR`
and `srcA=ONE`, `dstA=ZERO` - which is consistent with the core path using `PrimFs` and its
`BlendColor` output. So the dual-source design really is what runs, and it is not a leftover.

**`srcRgb=0x88F9` and `dstRgb=0x8589` are `GL_SRC1_COLOR` and `GL_SRC1_ALPHA`.** The first reading of
these was wrong because the sampler's own factor table used the older `EXT` numbering, where
`SRC1_COLOR` is `0x8589`; in `ARB_blend_func_extended`, which is what a GL 4.5 driver reports,
`GL_SRC1_COLOR` is `0x88F9` and `GL_SRC1_ALPHA` is `0x8589`. Decoded, the factors are exactly
`BlendFuncSeparate(Src1Color, Src1Alpha, One, Zero)` from `GlCore.cs:887` - source colour from the
shader's second output, source alpha from it as the destination factor.

**That is the important structural fact.** `SetBlend` sets the *uniform* `uBlend`; it does not touch
the GL blend factors at all. So every blended draw runs with the same fixed GL factors, and the entire
blend is carried by the shader's `BlendColor` output, which is `stp > 0.5 ? uBlend : uBlendOpaque`.
What a primitive blends by is therefore decided **per texel by the texture's STP bit**, not by the
draw's blend mode. A flat primitive has no texel, so per-texel selection cannot apply to it at all.

#### Retracted: the blend arms are not inverted

`GlShaders.cs:223` reads `BlendColor = texel.a >= 0.5 ? uBlend : uBlendOpaque;` while lines 188 and
215 read `BlendColor = stp > 0.5 ? uBlend : uBlendOpaque;`, and that looked like the arms had been
swapped on the main texture path. It was written up as a root cause, the swap was applied, and the
frame did not change - 644 non-black pixels against a 585 baseline, which is noise.

Re-reading the two paths shows why, and they are not comparable. At 188 and 215, `stp` is
**recomputed** as `texel.a < 0.95 ? 1.0 : 0.0`, an alpha used as a 0..1 coverage value for the
replacement-texture paths, and `stp > 0.5` means semi-transparent. At 223, `texel.a` **is the STP bit
itself** - the PlayStation stores semi-transparency as a bit, surfaced here as alpha 0 or 1 - so
`texel.a >= 0.5` already means semi-transparent and `? uBlend : uBlendOpaque` is correct. The two
lines use different alpha semantics; they are consistent, not inverted.

The swap was reverted and the checkout verified clean, and the baseline reproduced. This is the
fourth mechanism written up as a finding and withdrawn, and the second that was derived by comparing
two pieces of code without first establishing that their inputs mean the same thing.

#### The blend chain is verified correct, end to end

The question left open above - how a semi-transparent flat primitive's blend reaches the shader when
it has no texel to carry an STP bit - is answered, and the answer removes the concern:

```glsl
if (texMode == 4) {                                  // the untextured path
    FragColor = vec4(quant5(ivec3(vColor.rgb * 255.0 + 0.5)), uSetMask);
    BlendColor = uBlend;                             // selected unconditionally
    return;
}
```

`texMode == 4` is the flat path and it sets `BlendColor = uBlend` outright, so a flat primitive
blends by its draw's mode rather than by a texel. And `SetBlend` is what fills that uniform:

```csharp
private void SetBlend(float src, float dst) => _gl.Uniform4(_uBlend, src, src, src, dst);
```

So `BlendColor.rgb` is the RGB source factor and `BlendColor.a` the RGB destination factor, and
against the fixed GL factors `(Src1Color, Src1Alpha, One, Zero)` every mode evaluates correctly:

| mode | `SetBlend` | `uBlend` | result |
|---|---|---|---|
| 0 average | `(0.5, 0.5)` | `(0.5,0.5,0.5,0.5)` | `0.5 x src + 0.5 x dst` |
| 1 add | `(1, 1)` | `(1,1,1,1)` | `src + dst` |
| 3 add/4 | `(0.25, 1)` | `(0.25,0.25,0.25,1)` | `0.25 x src + dst` |
| 2 subtract, pass 1 | `(0, 1)` | `(0,0,0,1)` | `dst`, unchanged |
| 2 subtract, pass 2 | `(1, 1)` | `(1,1,1,1)` | `dst - src` under `FUNC_REVERSE_SUBTRACT` |

Textured paths select between `uBlend` and `uBlendOpaque` per texel; the flat path uses `uBlend`
outright; blending is disabled entirely for non-semi-transparent draws, where a stale `uBlend` cannot
matter. **The blend chain is correct.** Five rounds were spent across the blend path and this is the
one part of the frame that can now be called verified rather than un-blamed.

#### No flat primitive is drawn after the room, so the effect is state

The order question was asked of the screen-covering quads first and of every flat primitive second:

```
flat primitives (all)    : 858 (632 semi-transparent, 362 spanning >= 300x200)
textured after last flat : 121657 (max 121657)
flat colours (all)       : #000000:380, #808080:157, #FFFFFF:8, #080808:5, #101010:5, #181818:5
```

**121,657 textured draws follow the last flat primitive of any kind.** There is no late overlay.
Every flat primitive this game draws happens early, during the title and character-select screens,
and the post-transition room is drawn entirely by textured primitives.

**And removing those early flat primitives is still the one thing that restores the frame.** Those two
facts together admit only one kind of explanation: the flat primitives are not covering anything and
are not in the wrong order - they leave *state* behind that persists across the 121,657 textured
draws which follow. Nothing geometric can survive that many intervening draws.

This also explains, at last, why every sub-class suppression failed. Suppressing 380 black flat
primitives does not restore the frame; suppressing 157 mid-grey ones does not; suppressing 2,352
subtract-blended ones does not; suppressing 226 flat blend-0 ones does not. Only suppressing all 858
does. **No sub-class can work, because no sub-class is the cause.** The cause is something that
happens when flat primitives are drawn at all, and it is carried forward in the backend's state.

#### The defect is an erasure, not a failure to draw

Every measurement so far counted only the **displayed** buffer's contents at the end of the run. That
was wrong twice over, and fixing it changes what the problem is.

Wrong because the display origin and the draw target are **opposite** in every sample - which is
correct double buffering, but it means those counts were always reading the front buffer, the one not
being drawn into. A black front buffer over a correct back buffer is indistinguishable from a black
frame if only the front is ever counted.

And wrong because a final value is not a peak:

```
DEFAULT (black)
  framebuffer (0,0  ): gpu-dirty 37/39, non-black now 0,     peak 61135
  framebuffer (0,240): gpu-dirty 37/39, non-black now 0,     peak 61135

--skip-draws flat
  framebuffer (0,0  ): gpu-dirty 30/39, non-black now 67603, peak 67603
  framebuffer (0,240): gpu-dirty 29/39, non-black now 67603, peak 67603
```

**In the default configuration both framebuffers reach 61,135 non-black pixels - 79.6%, exactly the
uploaded-background figure - and then fall to zero.** They are not failing to receive content. The
content arrives, and is then **erased**. With flat drawing suppressed the same buffers end at 67,603
and never lose it.

So the question this document has been asking for many rounds - why the textured draws produce no
pixels - was the wrong question. They do produce pixels; the frame is filled and then wiped.

That also removes the last puzzle about the composition figures, because they now read as a sequence
rather than as alternatives: uploads fill both buffers to 79.6%, and either the textured detail is
added on top for 88% or the buffers are wiped back to zero.

#### When the erasure happens

```
 0,0s  buffer(0,0)      0   buffer(0,240)      0
 3,0s  buffer(0,0)  31801   buffer(0,240)  31801     title screen fills BOTH
 9,0s  buffer(0,0)  61135   buffer(0,240)  61135     both reach 79.6%
19,0s  buffer(0,0)  61135   buffer(0,240)      0     (0,240) wiped first
19,5s  buffer(0,0)      0   buffer(0,240)      0     then (0,0)
```

Only five changes in 28 seconds. Both buffers fill together - 31801 at 3 s, the title image, then
61135 at 9 s - and then at 19 s and 19.5 s both are wiped to zero, one buffer at a time, half a second
apart. Neither refills. The wipe lands at the stage transition.

Half a second apart is roughly fifteen frames, so these are two separate events rather than one
operation covering both buffers, which is worth noting for whoever picks this up.

**A caveat that limits what this can conclude.** The sampler runs about twice a second, so a
fill-and-wipe cycle inside a single frame would also read as a constant zero - the timeline collapses
runs, and it cannot see inside a frame. So "the erasure is a one-off" is **not** established by this
measurement, and the discriminator it was meant to provide - one-off pointing at target eviction,
per-frame pointing at a fill or a stale writeback - has not actually been applied.

Getting it would need sampling synchronised to the frame rather than to a wall clock: reading both
buffers at a fixed point in each frame, as the guest's `VSync` gives, and comparing consecutive
frames. That is a change to when the sample is taken, not to what is sampled.

#### Per-frame sampling settles it: the erasure is one-off

The wall-clock sampler could not see inside a frame, so a per-frame sampler was added, reading both
framebuffers at the guest's `VSync` every second frame. Six changes in 868 frames:

```
    frame    2   buffer(0,0)     0   buffer(0,240)     0
    frame   98   buffer(0,0) 31801   buffer(0,240) 31801     title image
    frame  276   buffer(0,0) 31801   buffer(0,240) 61135     select image
    frame  278   buffer(0,0) 61135   buffer(0,240) 61135     both at 79.6%
    frame  596   buffer(0,0)     0   buffer(0,240) 61135     (0,0) wiped
    frame  598   buffer(0,0)     0   buffer(0,240)     0     (0,240) wiped
```

Four things follow, and the third and fourth are new.

**The erasure is not per-frame.** Between frames 278 and 596 both buffers hold 61135 continuously,
across more than three hundred sampled frames, with no clearing at all. A per-frame fill or a
per-frame stale writeback would show as constant alternation. There is none.

**It happens once, at frames 596 and 598, two frames apart.** This is exactly the discriminator the
earlier measurement could not apply: a one-off wipe points at a **render target being destroyed and
recreated**, not at a fill and not at a stale whole-surface writeback, because those two would recur
every frame.

**The background is uploaded once, not per frame.** Six changes across 868 frames means the framebuffer
contents are written a handful of times in the entire run. So the room background is uploaded at the
room load and never again - which is why a single wipe is permanent, and why the frame stays black for
the remaining 270 frames rather than recovering.

**Both buffers are wiped separately, two frames apart.** Frame 596 takes `(0,0)` while `(0,240)` keeps
61135, and only at 598 does `(0,240)` follow. So these are two events targeting one buffer each, which
is consistent with per-buffer render targets being destroyed and recreated rather than with a single
operation on both.

#### Eviction is eliminated, and it was this document's own last hypothesis

`GetOrCreateRt` was the leading candidate last round, because it is the only path that destroys a
render target and a room load is exactly the kind of event to force one. Instrumenting the lifecycle
refutes it outright:

```
render target lifecycle   : 2 event(s)
    frame      3  create  slot1 (0,240) 320x240 margin=0 live=1
    frame      4  create  slot0 (0,0)   320x240 margin=0 live=2
```

**Two events, both creations, at frames 3 and 4 - and no evictions at any point in the run.** The two
framebuffer targets are made once, at the correct rectangles, and live for all 868 frames. The wipe at
frames 596 and 598 therefore coincides with no eviction, and the seeding question that went with it
does not arise: `SyncRtFromVram` is never called again after frame 4 to seed a replacement, because
there is never a replacement.

That is the fifth mechanism proposed here and withdrawn, and the third in a row withdrawn in the round
after it was written.

#### What is left

The candidates are now constrained from both sides. The wipe is **one-off**, so it is not a per-frame
fill and not a per-frame stale writeback. And it coincides with **no render target event**, so it is not
eviction. What remains are the two operations that write a whole buffer once, without being primitives
and without touching render targets:

- **`FillRect`** - reached from GP0 0x02, writes `_vram` through `_vram.Fill(...)` and writes the
  software shadow in `GpuCommands`, so a single screen clear would wipe **both** stores at once.
- **`CopyVram`** - reached from GP0 0x80, and likewise writes both stores.

Either explains what a per-frame fill and an eviction cannot: the two stores agree with each other in
every configuration, which is what a CPU-side operation writing both would produce. And either explains
why the wipe is permanent - the room background is uploaded **once** in the whole run, so a clear that
lands after that upload destroys it and nothing restores it.

The order question is therefore the whole question now: whether the game's own clear happens before or
after the room content arrives, and why suppressing flat drawing changes which.

#### Next probe

Log `FillRect` and `CopyVram` with their rectangles and colours, timestamped against the guest frame,
and read both stores immediately either side of frames 596 and 598. Both stores are written by these
operations, so a single entry showing both falling to zero at the same frame identifies the operation
and settles whether the clear precedes or follows the room's arrival.

#### The wipe is a CPU upload, and it happens every frame

That probe was run, and it answers the question while refuting two of this document's own conclusions.

```
    frame    594  (0,0)  61135  (0,240)  70483   upload (272,240) 16x240
    frame    595  (0,0)  61135  (0,240)      0   upload (0,240) 320x240
    frame    596  (0,0)  61135  (0,240)      0   upload (0,0)   320x240
    frame    597  (0,0)      0  (0,240)      0   upload (0,240) 320x240
    frame    598+  every frame: upload (0,240) 320x240 then upload (0,0) 320x240
```

**The operation is a CPU upload, not a fill and not a copy.** The guest loads a full 320x240 image
into each framebuffer, alternating between them, and from frame 595 onward it does so **every frame**.
Each upload takes the region's non-black count to zero, so what it loads is black.

**So "one-off" was wrong, and "per-frame" is right.** The per-frame sampler of two rounds ago reported
six changes in 868 frames and this document drew a one-off erasure from it. That reading was an
artefact of the timeline collapsing runs of equal readings: once the buffers are black, an upload that
writes black every frame produces no *change*, so a per-frame wipe is invisible to a change log. The
caveat recorded at the time - that the instrument cannot see inside a frame - was the right caveat, and
it was not carried far enough: the collapse also hides repetition across frames, not just within one.

**And the candidate set was wrong too.** `FillRect` and `CopyVram` were named as the only remaining
whole-buffer writers, on the reasoning that they write both stores. Uploads do as well, and were
overlooked - `StoreImageHalfword` writes the software shadow and `HleLoadFlush` forwards to the
backend, so an upload writes both stores exactly as a fill or a copy would.

#### What this actually means

The per-frame black upload is almost certainly **the game's own screen clear**, done through `LoadImage`
rather than GP0 0x02 - which is a normal technique, and the reason no fill was ever observed. If that is
what it is, the clear is legitimate and the room is supposed to be drawn on top of it every frame.

That makes the ordering question sharper rather than answering it. Each frame the guest clears both
buffers and then draws, and with flat drawing suppressed the result survives at 88% while with it
present the buffers read zero. Two possibilities remain, and they are now easy to tell apart:

1. **The room's draws precede the clear in the command stream**, so the clear erases what was just
   drawn. The fix would be an ordering one, and the flat primitives would be a red herring that merely
   shifts timing.
2. **The room's draws follow the clear but produce nothing** - which is where this document stood
   several rounds ago, and which the measured 79.6%/88% composition argues against, since the 8.4%
   that textured drawing adds is demonstrably produced when flat drawing is suppressed.

The next measurement that separates them is the obvious one: note the guest frame at every `LoadImage`
of a framebuffer region *and* at every textured draw landing in one, and read out which comes last
within a frame. That is a small extension of the instrumentation now in place, and it decides between a
one-line ordering fix and a return to the draw path.

#### The fork is resolved, and it refutes the ordering fix

```
  in-framebuffer textured draws after the last framebuffer clear, at each frame end:
    min 0, max 36620, frames 700
    frame    689     301 textured draw(s) after the clear
    frame    700     305 textured draw(s) after the clear
```

**About 301 in-framebuffer textured draws are issued after the clear, every frame, consistently.** So
the room's draws follow the per-frame black upload rather than preceding it. Possibility 1 - that the
clear erases what was just drawn, and the fix is an ordering one - is **refuted**. The draws are issued,
in the right order, after the clear, and the frame is still black.

#### And the modulation colour is the strongest lead yet

The one value never measured for the primitives that actually draw the room:

```
  textured colours (in-fb): #292929:20811, #26262B:18039, #808080:3406, #646469:2973, #5A5A5F:2835
  in-fb textured raw      : 0 raw, 206094 not raw
  ... raw colours         : (none)
  ... non-raw colours     : #292929:20811, #26262B:18039, #808080:3406
```

**Not one of the 206,094 in-framebuffer textured primitives is raw.** That matters because
`GlCore.V()` forces a raw primitive's modulation colour to neutral 128 and otherwise uses the vertex
colour as given:

```csharp
var raw = f.Textured && f.RawTexture;
float cr = raw ? 128f : v.R, cg = ..., cb = ...;
```

So for every one of these, the vertex colour is applied as a modulation, and the dominant colours are
**41 of 255 and 38 of 255** - roughly a sixth of full brightness, with only 3,406 of about 48,000
carrying a neutral 128.

The shader multiplies a texel by that modulation and quantises to five bits:

```glsl
ivec3 t8 = ivec3(texel.rgb * 31.0 + 0.5) << 3;
ivec3 c8 = (t8 * ivec3(vColor.rgb * 255.0 + 0.5)) >> 7;      // vColor/128 as a multiplier
FragColor = vec4(quant5(c8), ...);
```

At a modulation of 41, that multiplier is 0.32, and `quant5` then keeps only five bits - so a texel has
to be brighter than about an eighth of full scale before it survives as anything but zero. That is the
combination this document has spent many rounds looking for: **geometry correct, texture addressing
correct, sampled data present, blend chain correct, draws landing, draws in the right order - and a
black result, because the modulation collapses the output to zero.**

#### Refuted: the modulation colour is not what loses the frame

The modulation reading above was the strongest lead this document had, and it is wrong. Forcing neutral
modulation for every textured primitive, as if each were raw, changes nothing:

```
DEFAULT:               [gl] display crop  581 / 76800 (0.8%)
--neutral-modulation:  [gl] display crop  588 / 76800 (0.8%)
```

So the room is black even when every texel is modulated at full brightness, and the dark modulation
colours are a coincidence of what the game sends rather than the cause. That is the sixth mechanism
proposed here and withdrawn.

It is worth being precise about what that leaves, because the list is now long and one item on it is
inconsistent with the others. The textured draws that paint the room have correct geometry, correct
texture addressing, sampled data that is provably present, a blend chain that evaluates correctly,
full-brightness modulation, they land in the framebuffer, and they are issued in the right order after
the per-frame clear. Every one of those has been measured rather than argued. And they produce nothing.

The one fact that does not fit any partial explanation remains the same: **suppressing flat drawing
makes those same textured draws produce 88%.** A state effect is still the only shape that fits, because
nothing local to the textured draws explains why removing unrelated earlier primitives would change
their output.

#### Where to look next

With the colour path cleared, the remaining difference between a textured draw that works and one that
does not is what the backend does with it, and `HleTri` reaches the backend by two different routes.
`InterpBackend` records the triangle and replays it later into `GlCore` - and the recording carries
`PrimFlags`, including `Textured`, `RawTexture`, `SemiTrans`, `TPage` and `Clut`, but the replay goes
through `Emit` and `ReplayTri`, not through the path a direct call to `GlCore` would take. A field lost
or altered on the way through the recorder would be invisible to every measurement made from outside
the backend, because those measurements read state that the recorder has already passed.

The next probe is to compare the `PrimFlags` recorded by `InterpBackend` against the `PrimFlags` the
event carried, for one framebuffer-resident textured primitive. That is the last place in the chain
that has never been inspected, and it is the only one that can explain a draw arriving correctly and
rendering nothing.

#### The recorder is faithful, so it is cleared too

It did not need instrumenting, only measuring what is switched on:

```
interpolation : targetFps=0 requested=False pgxp=False/False available=False enabled=False effective=0
```

Interpolation and PGXP are **both off**. That matters because `InterpBackend.ReplayTri` has two paths
and only one preserves the recorded draw:

```csharp
if (previous == null || weight >= 1f) { Emit(in tri, in tri.A, in tri.B, in tri.C); return; }
if (tri.Transform > 0 && _transforms.Warp(tri.Transform, in tri.A, in tri.B, in tri.C, out var wa, ...))
    { Emit(in tri, in wa, in wb, in wc); return; }      // vertices REPLACED
```

With interpolation off, `previous` is null, so the first branch is taken: `Emit` replays the recorded
vertices, re-adds the offset through `Attach`, and passes `tri.Flags` through untouched. The warping
branch cannot be reached either, because `GpuRaster` sets
`v[i].Transform = transform != 0 ? transform : -1` and PGXP - which is what populates it - is off, so
`Group` yields no transform and `tri.Transform` stays 0.

So the recorder is a faithful record-and-replay layer for this game, and the last uninspected component
in the chain is cleared. `uScale`, the other uniform never seen being assigned, is set by
`SetScaleUniform` to `GlVram.Scale`, so that is cleared as well - and it died before being tested, which
is the right order for once.

#### The shadow is not evidence about rendering, and much of what was read from it is void

Two measurements in this project have been right about *what* they sampled and wrong about *when* -
the "one-off erasure" and the "silent audio". Applying that lesson to the black frame was the obvious
next step, and it does not rescue it. But it does expose a mistake of a different kind, in *what* was
being sampled.

```
DEFAULT:    backend store (0,0):   non-black now 581,   peak 31906  (41.5%)
            backend store (0,240): non-black now 578,   peak 31906
skip-flat:  backend store (0,0):   non-black now 67559, peak 67601  (88.0%)

shadow:     non-black now 0, peak 61135, in both configurations
```

**The backend's peak in the default configuration is 31,906, which is exactly the title screen's 41.5%
- an uploaded image.** The room, which is over 67,000, never reaches the backend store at any point in
the run. So the room genuinely does not render, the endpoint was not merely a bad moment, and the
"When" hypothesis is dead for this defect.

**What that also shows is that the software shadow is not evidence about rendering at all.** The shadow
receives only CPU writes - uploads, fills and VRAM copies - plus what a guest readback copies into it.
Draw output goes to the backend store and never touches the shadow. So the shadow's contents record
what the CPU put in VRAM and nothing about what was drawn.

That matters because several earlier conclusions in this document were drawn from shadow figures:
that the framebuffers "reach 61135 and then fall to zero", and the whole "the defect is an erasure"
reframing built on it. Both describe **uploaded** content only. The guest uploads a black 320x240 image
to each framebuffer every frame from frame 595 onward, so the shadow's zero is exactly what those
uploads produce, and it never was evidence of anything being destroyed.

The reframing is therefore not wrong so much as misattributed: something *is* being lost between the
title screen and the room, but the shadow cannot see it and never could. The backend store can, and
what it shows is that the room's drawn output never arrives there.


#### The render target itself is empty: the failure is rasterisation

Every reading in this investigation until now came from VRAM. Draws do not go to VRAM - they go into a
render target and reach VRAM only through `Writeback` - so the two remaining possibilities had never
been separated. Reading the target's own pixels separates them:

```
DEFAULT (black)
  backend store (0,0)   : now 581,     peak 31906      = 41.5% of 76800
  render target surface : now 9299,    peak 510496     = 41.5% of 1228800
  target as luminance   : essentially blank

--skip-draws flat (renders)
  backend store (0,0)   : now 67558,   peak 67609      = 88.0% of 76800
  render target surface : now 1080879, peak 1081702    = 88.0% of 1228800
  target as luminance   : the room, fully drawn
```

**The target and the backend agree in both configurations**, as percentages of their respective areas.
So the writeback works, and it is exonerated along with everything at VRAM level: `Writeback`,
`SyncRtsFromVram`, the target lifecycle, the two-store plumbing, and every mechanism that acts on VRAM
rather than on the drawn surface.

**The room's draws produce nothing inside the render target.** In the default configuration the target
holds only the uploaded title image and nothing else; with flat drawing suppressed it holds the room at
88%, captured to `out/diagnostics/target-surface-ascii.txt`, and that is the first time this project has
actually seen this game render.

That is a real narrowing and it is also a retraction. Several rounds were spent on mechanisms that
operate on VRAM - the sync-versus-writeback ratio, the two stores disagreeing, whole-region transfers -
and all of them were looking one stage too late in the pipeline. The question is now specifically why
`GlCore`'s batched draw rasterises nothing for these primitives, and why the presence of flat
primitives changes that.

#### Retracted: alpha does not count drawn pixels

The previous revision reframed the target as "filled with black rather than empty", reasoning that the
fragment shader writes alpha as `max(stp, uSetMask)` and therefore a drawn pixel has alpha set. That
reasoning is wrong and the reframing is withdrawn.

An **opaque** texel has `stp = 0`, so a fragment drawn from one gets `alpha = max(0, uSetMask) = 0`
unless the mask is set. Alpha is therefore set only for fragments drawn from *semi-transparent* texels,
or drawn with the mask bit. A room drawn entirely from opaque texels - which is what a pre-rendered
background is - would have `alpha = 0` everywhere and would count as zero under this probe while being
fully drawn.

So the alpha count is not a measure of what was drawn, and the numbers cannot carry the conclusion that
was placed on them:

```
                                   failing config      working config
target pixels non-black, now       9,299               1,080,879
target pixels alpha-set, now       953                 211,193
target pixels alpha-set, peak      718,240             719,920
```

All they show is that the working configuration has semi-transparent fragments and the failing one has
almost none - consistent with the room's semi-transparent elements being drawn in one case and not the
other, and silent about its opaque ones.

**The colour-based conclusion therefore stands unchanged**: in the failing configuration the target holds
9,299 non-black pixels of 1,228,800, essentially nothing, against 1,080,879 in the working one. For
opaque draws colour is the right measure and alpha is not, and a pre-rendered background is opaque.

That is the fourth probe here to rest on a premise that did not survive checking, and the third to be
withdrawn in the round after it was written. The pattern is consistent enough to name: an aggregate is
measured, a meaning is assumed for it, and the assumption - not the measurement - is what turns out to
be false. Alpha "counts drawn pixels" was such an assumption; so was "a peak describes the end state".


The colour count said the render target was empty. It is not. The fragment shader writes alpha as
`max(stp, uSetMask)`, so counting pixels whose alpha is set counts what was **drawn** regardless of
what colour came out - and that count is nearly identical in both configurations:

```
                                    failing config      working config
target pixels non-black             9,299               1,080,879
target pixels with alpha set        718,240 (peak)      719,920 (peak)
draw environments                   3 distinct          2 distinct
  texture window                    mask=(0,0) off=(0,0) mask=(0,0) off=(0,0)
  clip                              (0,240)..(319,479)   (0,240)..(319,479)
```

**Both configurations rasterise.** Roughly 718,000 of 1,228,800 target pixels are written with alpha
set in each - 58% of the surface. So the room's draws are filling the target and the fragments come out
**black**, which a colour-counting probe cannot distinguish from an empty surface.

That invalidates a conclusion drawn one round earlier, and it changes where the fault is. The
rasteriser is fine, the vertices are fine, the clip is fine, the texture window is the identity in both
(`mask=(0,0) off=(0,0)`, so the sample coordinate is unmasked), and the blend chain was verified
earlier. What differs is only the colour the fragment ends up with.

Since `quant5(c8) == 0` implies `(t8 * vColor) >> 7 < 8`, a zero result needs either a nearly black
texel or a very dark modulation. Forcing neutral modulation changed nothing, which was verified
without disturbing the texpage, so the modulation is not it - which leaves the **sampled texel**.

#### Retracted: the target read duplicates the backend read

Six identical runs were compared to check whether the black frame is deterministic or a race. It is
deterministic - the figures vary by 0.3% and only with the frame count - but the table exposes a worse
problem with the measurement itself:

```
run  target(0,0)  1,228,800 px   backend(0,0)  76,800 px   as percentages
1      193,574                    12,104                    15.8%  /  15.8%
4      194,068                    12,091                    15.8%  /  15.7%
earlier 9,299                       581                     0.76%  /  0.76%
flat 1,081,681                    67,558                    88.0%  /  88.0%
```

**The target's non-black count is always the backend's, as a percentage of each one's area** - across
runs and across configurations. That is not a coincidence: `SyncRtsFromVram` copies VRAM into any
intersecting target after every upload and every VRAM copy, so the target is kept in step with VRAM and
a sample of it reports VRAM's contents.

So the render-target read does not measure what was drawn. It measures the same quantity as the backend
read, twice, and **the "breakthrough" two rounds ago compared those two readings with each other and
treated their agreement as evidence** when their agreement is exactly what the sync guarantees.

The conclusion that survives is the weaker one: the failing configuration's target holds 0.76% at one
point in the frame cycle and 15.8% at another, so the figures are also sensitive to *when* in the
per-frame clear-and-upload cycle the sample lands. That is the same timing sensitivity that has caught
this document out before.

This is the fifth premise here to be withdrawn, and the second in two rounds.

#### The room renders. It is composited before it is drawn, about half the time

Measuring the target inside `Writeback` - the moment it is composited into VRAM, where drawn content is
unambiguous and cannot be VRAM's - gives the most informative result of this investigation:

```
                                   samples   entirely black   mean of 10240   best of 10240
DEFAULT        origin (0,0)           295       133  (45%)          -            9248  (90.3%)
               origin (0,240)         296       136  (46%)          -            9248  (90.3%)
--skip-draws flat  origin (0,0)       226         0   (0%)         7388           9248  (90.3%)
                   origin (0,240)     224         0   (0%)          -            9248  (90.3%)
```

Three things follow, and the first is the important one.

**The room renders.** Both configurations reach a best of 9,248 of 10,240 - **90.3%** - at writeback time.
Whatever else is wrong, the draws, the texture sampling, the rasterisation and the compositing all
work, and have been working throughout. Every measurement before this one that concluded the room was
not drawn was measuring something that could not distinguish drawn content from VRAM's contents.

**It is not one double-buffer target failing.** Both origins behave identically in each configuration,
45% and 46% black against 0% and 0%. The room reaches both buffers or neither, so this is not a
buffer-selection or classification problem.

**It is temporal.** In the failing configuration roughly 45% of writebacks composite an empty target;
in the working configuration none ever do. A writeback that composites nothing can only be one that
happens *before* the frame's draws - after the sync that seeds the target from a freshly cleared VRAM,
and before anything has been drawn into it. So in about half the frames the compositing precedes the
drawing, and in the other half it follows it.

That order is not the guest's to get wrong: the guest issues its commands in a fixed sequence. What
varies is when the deferred layer gets to replay them. `InterpBackend` records draws into a frame graph
and replays it on `Publish`, driven by the guest's `VSync`, feeding `_ready` and then `_current` through
`Acquire`. A guest readback arriving between `Publish` and the next replay therefore flushes targets
that do not yet contain the current frame's draws - and `InterpBackend.ReadVram` does call `Settle()`,
but `Settle` replays `_current`, which is the *previously published* graph, not the one still being
recorded.

That is the shape of the defect: a mid-frame flush racing the replay, decided by timing, which is also
why removing a class of primitives changes it. Flat primitives change when in the frame the drawing
finishes relative to the flush without changing the ordering that is actually at fault.

Read the two functions together and it is more specific than that. `InterpBackend.ReadVram` settles
first, and `Settle` begins by returning early if the graph is empty:

```csharp
public void ReadVram(int x, int y, int w, int h, Span<ushort> px)
{
    Settle();
    _inner.ReadVram(x, y, w, h, px);      // which flushes dirty targets
}

private void Settle()
{
    lock (_gate)
    {
        if (!_active) return;
        if (_current.IsEmpty) return;     // nothing to replay
        Replay(_current, null, 1f);
        _current.Clear();                 // and now it IS empty
    }
}
```

So the **first** readback in a frame replays `_current` - the graph published at the last `VSync` -
and clears it; the **second** readback in the same frame finds it empty, replays nothing, and flushes
targets that the current frame's still-in-flight `_recording` has not been drawn into yet. Those
flushes composite an empty target, which is exactly the 45% of writebacks measured as entirely black.

That accounts for every part of the observation at once: why the room renders at 90.3% and still shows
black, why the split is near half rather than total, why both double-buffer targets behave identically,
and why removing a class of primitives - changing when in the frame the draws finish - changes the
outcome without addressing the cause.

#### The black writebacks are the end-of-frame flush, and settle is not the cause

Two measurements, and the first refutes the mechanism proposed above.

`Settle` is called **seven times** in an entire run - 1 finding the graph empty and 6 replaying in the
failing configuration, against 2 and 5 in the working one. That does not correlate with 269 black
writebacks in any way, so the mid-frame-flush-versus-replay mechanism is **wrong**. It was a good fit
for the shape of the evidence and it is not the cause.

Labelling the four ways a target can be written back does identify the cause, though:

```
DEFAULT        dirtyIntersecting (0,0)     1 sample,   1 entirely black, best    0
               dirtyIntersecting (0,240)   5 samples,  2 entirely black, best 5618
               frameEnd          (0,0)   292 samples, 134 entirely black (46%), best 9248
               frameEnd          (0,240) 292 samples, 134 entirely black (46%), best 9248

--skip-draws flat  frameEnd     (0,0)   222 samples,   0 entirely black, best 9248
                   frameEnd     (0,240) 222 samples,   0 entirely black, best 9248
```

**268 of the 270 black writebacks come from the end-of-frame flush**, which writes back every target
still marked dirty. A target is only dirty if something *drew* into it during that frame, since
`Writeback` clears the flag and a VRAM sync does not set it. So these are not stale targets being
flushed: each one had drawing done into it during the frame that produced it, and the result was black.

Each buffer is black in 46% of its own frame-end flushes, and both figures match. With two buffers
flushed per frame, that is the signature of **one buffer per frame being drawn with the room and the
other being drawn black**, alternating - which is what double buffering looks like when the front buffer
is not left holding the previous frame's image.

The working configuration never does this: both buffers are drawn with the room in every frame.

So the defect is narrower than "the room does not render" and different from it. The room renders into
one buffer; the other buffer receives drawing that produces black; and they swap, so the displayed
buffer is the black one about half the time - which is why an end-of-run sample of the display shows a
black frame, and why the frames that would show the room are not the ones being measured.

#### Corrected: the alternation is across frames, not across buffers

The previous revision read 46% black in each buffer as one buffer being drawn with the room and the other
with black, alternating. Counting the draws that reach each buffer refutes that:

```
DEFAULT        target y=0     840 flush(es) carrying 343,197 vertices
               target y=240   837 flush(es) carrying 343,929 vertices
               target y=-1      6 flush(es) carrying      36 vertices

--skip-draws flat
               target y=0     644 flush(es) carrying 342,321 vertices
               target y=240   656 flush(es) carrying 347,187 vertices
```

**Both buffers receive the same drawing, within 0.2% of each other**, so the room's draws are not going to
one buffer preferentially. `target y=-1` is the path where no target was classified and the draw went
into full VRAM, and it carries 36 vertices in the whole run.

Since each buffer is black in 46% of its *own* frame-end flushes, and both receive the same draws, the
46% cannot be a split between buffers. It is a split between **frames**: in about 46% of frames both
buffers come out black despite having been drawn into, and in the remaining 54% both hold the room. The
working configuration has no black frames at all.

So the defect is a **frame-level** alternation. The same draws reach the same targets every frame, and
roughly every other frame the result is black - which is the strongest form of the question, because
nothing about the draws differs between a good frame and a bad one.

#### Refuted: the ordering mechanism, and what the graph composition shows instead

```
                                   graphs   region write after draws   draws after   draws only   writes only
DEFAULT                              638              7   (1.9%)           371           219            1
--skip-draws flat                    638              0   (0.0%)           313           136           66
```

A frame whose last whole-region write followed its last draw would composite its own black clear over
its own drawing, and roughly 46% of frames would have to show that order to account for 46% of frames
being black. **Seven of 638 do**, so the ordering mechanism is refuted. The draws are followed by the
region write in the great majority of graphs in both configurations, which is the correct order.

The composition difference is real and worth recording even though it does not yet explain anything.
In the default configuration almost every graph that contains a region write also contains draws - 378
of 379 - and there is essentially one graph in the whole run that writes regions without drawing. In the
working configuration that split is 66 to 313, with 123 graphs empty of both against 40 in the default.

So the working configuration's frames are far more fragmented: clears and drawing routinely land in
different published graphs, and a fifth of its graphs are empty. The failing configuration concentrates
both into the same graph almost every time. Nothing about that is obviously wrong - the order within a
graph is correct in both - but it is the largest structural difference found so far, and it is the kind
of difference that a timing-sensitive defect would produce.

This is the eighth mechanism withdrawn. Five of the last six were refuted by measuring the specific
thing the mechanism predicted, which is the right way to lose them, but it also means the reasoning has
been running ahead of the evidence for several rounds.

#### The blit is faithful, so the pipeline is verified end to end

```
                                   pairs   mean target   mean vram   target had content, vram empty
DEFAULT                              590       1897          2421                 0
--skip-draws flat                    450       7392          8679                 0
```

VRAM ends up holding *more* than the target's strip, which is expected since it holds other content as
well, and **in no case did a target with content produce an empty VRAM region**. The blit carries what
it is given, so it is exonerated.

That completes the pipeline. Every step from a guest GP0 command to a pixel in VRAM has now been
measured: the command decodes, the vertices carry correct positions, the clip contains them, the draw
is classified into the right target, the draw reaches the driver in the right order with the right
counts, the target is rendered into, and the blit copies it to VRAM faithfully. **The room reaches 90.3%
in 54% of frames and is black in the other 46%, and everything downstream of the drawing is proven
correct.**

So the draws are producing black in 46% of frames, before the blit, with the target already black. Every
part of the draw that can be measured outside the shader has been measured and matches between a frame
that renders and one that does not. What has not been measured is **what the shader samples** - the
texels and the palette it reads out of VRAM at the moment of the draw.

That is now the only remaining place, and it is measurable from outside: for the textured primitives
that land in a framebuffer, resolve the page base from the texpage the vertex carries, resolve the
palette address from the clut, and read both out of VRAM's backend store at the moment of the draw. If
those regions are black in a black frame and hold the room's data in a good frame, the sample is the
answer and the question becomes what empties them; if they are identical either way, the shader's own
arithmetic is all that is left.


#### Where thirty rounds of measurement leave this

Every component between a guest GP0 command and the framebuffer has now been measured rather than
argued, and every one is correct: geometry, texture addressing, the sampled VRAM contents, the blend
chain and its factors, the modulation colour, the drawing offset, the order of draws against the
per-frame clear, the landing of draws in the framebuffer, the render target lifecycle, and the
interpolation recorder. The room still does not render.

The one fact that survives all of it, and which no component-level explanation accounts for, is that
**suppressing flat drawing makes those same textured draws produce 88%**. That is a state effect whose
cause has not been found. The honest summary is that fourteen mechanisms have been proposed, six
withdrawn after measurement, and the remaining candidate space is the backend's behaviour as a whole
rather than any value that travels through it.


#### What can be said with confidence

Both buffers hold 79.6% of the frame - the uploaded background figure exactly - and end at zero, while
with flat drawing suppressed they hold 88% and never lose it. Whatever the mechanism, it destroys
content that was demonstrably present, rather than preventing it from arriving. That is the reframing
this round established, and it survives the caveat above: a per-frame wipe and a one-off wipe are both
erasures.


#### What it means for the search

An erasure has a different candidate set from a failure to draw, and a smaller one. Something that
writes whole buffers rather than pixels:

- **`GlCore.FillRect`** writes `_vram` directly through `_vram.Fill(...)` and additionally takes a
  `FillRtFull` path for render targets the rectangle covers. A fill of the framebuffer region would
  wipe it in one operation. Note this is reached from the GP0 0x02 fill, which `--skip-draws` does not
  intercept, so if a fill were responsible then suppressing flat drawing should not help - which means
  either the erasure is not a fill, or something else about the flat primitives gates it.
- **A render target being destroyed and recreated.** `GetOrCreateRt` evicts the oldest target, writes
  it back if dirty, and destroys it; a replacement is then seeded from `_vram`. If a target covering a
  framebuffer is destroyed while clean and recreated empty, `SyncRtFromVram` should refill it - and if
  that seeding is skipped or covers the wrong rectangle, its next writeback would lay emptiness over
  the buffer.
- **A stale target's writeback.** `Writeback` blits the whole target surface. A target that is clean
  but whose contents are empty would, on its next flush, overwrite whatever the buffer holds.

Since no flat primitive is drawn after the room, the erasure must be triggered by state left behind
earlier - so the question is now specifically which of these three writes whole buffers, and what the
flat primitives leave set that causes it.

#### Next probe

Sample the two buffers' contents on the fast cadence rather than only at the end, so the erasure can
be placed in time - whether it happens once or every frame, and whether it coincides with the stage
transition. That distinguishes a one-off wipe from a per-frame one, which separates the three
candidates above: a per-frame erasure points at a fill or a stale writeback, a one-off points at a
target being destroyed and recreated.


#### Where that leaves the search

Since the state survives 121,657 textured draws, it is set once and never reset, which is a much
smaller set of candidates than anything considered so far. The ones worth checking, in order:

1. **What is in the displayed VRAM region.** The black flat quads write black into the framebuffer
   early. If the room's textured draws are classified into a render target whose writeback does not
   cover the displayed region, then the region keeps that early black - and suppressing the flat
   primitives leaves whatever the uploads put there, which is the 79.6% baseline plus the textured
   detail that reaches 88%. That composition fits every measurement so far, including the two-store
   agreement, because both stores would be showing the same un-overwritten background.
2. **Render-target selection changing once flat primitives stop being drawn.** `Classify` picks a
   target from the display-rect ring by geometry, so this is only plausible if flat primitives
   influence `_env`'s clip - and `HleFill` notably does not call `SetDrawEnv` while `HleTri`,
   `HleRect` and `HleLine` do.
3. **A uniform left set.** `uBlendOpaque`, `uSetMask` and `uCheckMask` are assigned per draw, so they
   are unlikely; `uScale` and the replacement-texture uniforms are the ones not re-set each draw.

#### Next probe

Compare `GlCore`'s `_vram` contents against the display region at a moment when the room should have
been drawn: if the displayed region holds the early black rather than the room, the writeback is not
covering it, and the question becomes why the target's rectangle and the display's rectangle differ.
`GpuGlAccess.TargetOriginX` and `TargetOriginY` give the target's placement directly, and the sampler
already reads them - the missing piece is sampling them alongside the VRAM contents rather than on a
separate cadence.


#### The colours, measured

`RenderPrimEvent` carries no colour, so measuring it needed a hook in `GpuRaster`. That is a runtime
change and by this project's convention it lives in `patches/`, as
`patches/0001-gpu-diagnostics-primitive-colour.patch`: three public statics on `Gpu` recording the
colour of the most recently decoded primitive, set immediately before the render event is dispatched
at all three dispatch sites - polygons, rectangles and lines - so a listener reads the colour that
draw will use.

```
flat colours (all)      : #000000:378, #808080:157, #FFFFFF:8, #080808:5, #101010:5, #181818:5
flat colours (screen)   : #000000:226, #FFFFFF:5, #CFCFCF:3, #9F9F9F:3, #6F6F6F:3, #3F3F3F:3
screen-covering quads   : 360, after the LAST one: 120502
```

**378 of the 856 flat primitives are pure black**, and 157 more are exactly mid-grey `#808080`; 226
of the 360 screen-covering ones are black. So the flat class really is dominated by black and
mid-grey, which is what a screen clear and a 50% overlay look like.

**But the screen-covering quads are not the culprit**, and that is a new elimination. The count of
textured draws after the *last* screen quad is 120502, the same as the maximum, which means the
screen quads all occur early - during the title screens - and none is drawn after the room. So they
are clears for the title and character-select, not for the stage, and whatever turns the
post-transition frame black is among the **496 flat primitives that are not screen-covering**.

This also closes a gap in the earlier draw-order measurement, which reported only the maximum. The
maximum says "at least one quad had the room drawn after it"; only the final value says whether any
quad came last. Both are now reported.


#### The remaining unknown, and why it is narrow

Given a correct blend chain, correct geometry, correct texture addressing, correct sampled data and
draws that verifiably land, the thing not yet observed is **the colour of the flat primitives
themselves**.

That is a different question from every one asked so far, and it is the right shape. For a textured
primitive the vertex colour is only a *modulator*, so a colour defect would show as a tint.
For a flat primitive the vertex colour **is** the output. A colour fault therefore blackens the flat
primitives while leaving the textured ones looking plausible - which is exactly the split the
suppression experiment found, and the one asymmetry nothing else has explained.

The colour is also the one value that has never been measured, and it cannot be reached from where
the other measurements were taken. `RenderPrimEvent` carries `SemiTransparent`, `Clut`, `TexPage`,
`Raw` and `Gouraud`, but no colour, and `GpuGlAccess` exposes the target and the GL object but not the
vertex data. The colour exists in `GpuRaster` as it decodes the GP0 colour word, flows into
`HleVertex.R/G/B`, and reaches the shader as `vColor` - so observing it means instrumenting
`GpuRaster`, which by this project's own convention belongs in `patches/` rather than as a direct
edit to the checkout.

#### Next probe

Write a diagnostics patch that records the vertex colour of each primitive, alongside whether it is
textured and which blend mode is in force, and report the distribution for flat screen-covering
primitives specifically. If those quads are saturated when they should not be, a colour-decoding
fault is the answer and the whole black frame is explained. If their colours are ordinary, the defect
is elsewhere and the measurement at least closes the last unmeasured value in the chain.


#### What is suggested but not established

The blend equation in the black run is `FUNC_REVERSE_SUBTRACT` in five samples of six, and `FUNC_ADD`
in four of six in the correct run. That is the first difference in *observed* state between the two
configurations, and it matches the mechanism hypothesised earlier - a reverse subtract left in state
by a subtract-blended draw.

It is not enough to call it the cause, for two reasons. The sample is six readings taken on a timer,
so it reflects whatever draw happened to be in flight, and `FUNC_REVERSE_SUBTRACT` is the *expected*
transient state during a subtract-blended draw's second pass - seeing it mid-draw proves nothing. And
blending is disabled on the samples that matter, where a stale equation cannot affect the result.

Sampling on a timer cannot separate those. The next step is to sample at a **deterministic point** -
immediately after the guest's `VSync`, once the frame's draws are complete - and repeat enough times
to compare like with like. Only then does a difference in final state mean anything.

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
X. Audio was resolved after this list was written: `--verify-audio` measures the SPU producing
   output, peak 8619 of 32767, from a block mixed while voices carried volume. Continuous music
   is still unestablished, and CD and XA audio are idle throughout.
X. Determinism was resolved after this list was written too: `tools/Test-RecompileDeterminism.ps1`
   passes with all 10 files byte-identical across two recompiles, and fails correctly when a
   generated file is perturbed by hand. `generated/` may stay ignored.
X. All seven overlays were verified dispatching after this list was written as well, by
   `--verify-overlays`: 8 of 8, 0 failed, through the real LBA mapping and base-write promotion.


#### Refuted: the sampled texture, which leaves only the shader's own state

```
                                   non-black in the texture area (x >= 320)
DEFAULT                                    154,060 of 360,448
--skip-draws flat                          154,060 of 360,448
```

The texture area - where the shader reads its texels from, and which cannot overlap the framebuffer
because the tiles' pages resolve to x >= 320 while the framebuffer occupies x < 320 - is populated
**identically** in both configurations, to the pixel. So what the shader samples is the same in a frame
that renders and one that does not, and the sample is refuted as an explanation. That is the eleventh
mechanism withdrawn.

Worth recording too: the failing configuration's target holds 15.8% at the sampling instant rather than
nothing, which is the 46/54 split averaged. The room is not absent - it is present in 54% of frames and
absent in 46%, and an instantaneous sample of a varying quantity lands somewhere in between.

So the position is this. Between a frame that renders and one that comes out black, all of the following
are identical: the vertices and their positions, the clip, the classification into a target, the
operation order, the vertex counts, the sampled texture area, and the blit. The draws are not the
difference. What has not been measured is the **state `GlCore` applies when it flushes the batch** -
which target, and the transparency, blend, image and mask flags it hands the shader - because a batch is
drawn under the state of its last `Begin` rather than under per-triangle state.

#### Next probe

Capture `GlCore`'s batch state at each flush - `_kTarget`, `_kTransparent`, `_kBlend`, `_kImage`,
`_kSetMask`, `_kCheckMask` - and compare a frame that renders against one that does not. Every input to
the draw has now been shown identical between the two, so if any of these differs, that is the answer.
If none differs, then the shader produces different results from identical inputs, which is itself
informative and points at the program rather than at the data.

#### The batch state is identical too, and the defect is now stated exactly

Reading the target immediately after `DrawArrays` couples the state a batch ran under with what that
batch produced. The room's opaque batches - about 730 vertices each, one per frame per buffer - come out
as follows:

```
DEFAULT     target=(0,240) 320x240 wide1x=320 margin=0 y=240 transparent=0 blend=0 ... thread=2
            474 batch(es), 345,393 verts, empty after 241, best 10240
            target=(0,0)   320x240 wide1x=320 margin=0 y=0   transparent=0 blend=0 ... thread=2
            467 batch(es), 340,575 verts, empty after 237, best 10240

--skip-draws flat
            target=(0,240) ... 430 batch(es), 344,601 verts, empty after 0, best 10240
            target=(0,0)   ... 429 batch(es), 342,477 verts, empty after 0, best 10240
```

The state tuples are **identical** between the two configurations. So are the target's geometry
(`320x240`, `wide1x=320`, `margin=0`), the thread the batch runs on (one thread, id 2 throughout), the
vertex counts, the vertex positions, and - sampled at the moment of each draw rather than on a timer -
the presence of the texture area the shader reads:

```
target empty  with texture present 725   texture empty  72
target drawn  with texture present 728   texture empty 166
```

So the texture is present in nine cases out of ten in which the target nevertheless comes out empty.

**With every input that can be observed from outside the GL call shown identical, the room's batch
leaves the target completely empty in 51% of flushes in one configuration and 0% in the other.** Three
strips at widely separated rows were read rather than one, to rule out the metric being an artefact of
sampling a single band, and the result was unchanged; when a batch does work the strip is completely
full, `10240` of `10240`.

That is the defect, stated as precisely as measurement allows: **not a wrong value anywhere, but an
identical draw that produces pixels half the time.** Which is the signature of a race, or of state the
driver holds that the code does not model.

#### Next probe

Two things remain, and neither is reachable by reading VRAM or the runtime's own fields.

**The driver's state at the moment of the draw** - which program is bound, whether the vertex attribute
bindings still point at the buffer the vertices were uploaded to, and whether the uniforms actually
landed. A batch drawn with stale attribute bindings produces nothing while every value the runtime
believes it set is correct, which is exactly what has been measured. Querying the program, the vertex
array object and the attribute pointers through `GL.GetInteger` alongside the existing sample is the
direct way to see it.

**Whether the batch is genuinely the same batch.** The vertices are counted, not compared: two batches
of 730 vertices drawn at different times could differ in content while matching in count and in the
bounding range that has been checked. Hashing the vertex data at flush and grouping by hash, against
whether the target came out empty, would separate "same input, different result" from "different input,
same apparent shape".

#### The vertex contents differ, and the claim that they did not was never measured

Four rounds of reasoning rested on the statement that a batch which renders and a batch which comes out black have identical vertices. That statement came from comparing vertex counts and bounding ranges. Hashing the vertex bytes at flush and grouping the hashes by outcome shows it was wrong to rely on:

```n                                   large batches   distinct contents   set fingerprint
DEFAULT                                521              226            56FB547EE7ADD21E
--skip-draws flat                      530              228            0B141253FBDDEAA1
```

**The two configurations do not see the same vertex data at all.** Different fingerprints and different counts mean the batches reaching the driver are grouped differently depending on whether flat primitives are drawn, which is a stronger and more useful statement than anything the count-based comparison could support.

Within a configuration the grouping is stable: **no content appeared in both states** - 0 of 227 - so each batch's outcome is deterministic, which agrees with the determinism established earlier by repeating runs. In the failing configuration 172 contents render, 55 never do, and 3 of those are legitimately off-screen. The 55 carry 429 to 1617 vertices each, against roughly 730 for a frame's room, so they are not simply the room's batch.

Three things follow, and one hypothesis died on the way. It is not misclassification into the wrong buffer: of the 52 failing contents whose vertices intersect a framebuffer band, **0** lie in the band of the *other* buffer, so they are not being clipped away wholesale by a target that does not match them.

**A caveat on that last measurement, stated before it misleads anyone.** The band test compares a content's *range* against the target's band, not the distribution of its vertices within that range. A batch spanning y 0..1263 registers as intersecting the band while most of its geometry may be far outside it. So   mismatches shows the ranges are compatible, not that the vertices are inside - the same weakness that made a count and a range a poor substitute for contents in the first place.

#### Next probe

Characterise the 55 failing contents by something other than their range: the fraction of their vertices that actually fall inside the target's band, the texpage and clut they carry, and their per-primitive spans. A batch that is mostly outside its target with a few degenerate triangles inside would render nothing while looking perfectly reasonable in every aggregate measured so far.


#### The failing batches are real geometry, not an artefact

The measurement from the previous round used a content's y *range* against the target's band, which cannot tell a batch that reaches into the band from one whose range merely straddles it. Counting the vertices that actually fall inside the band corrects that, and the finding survives:

```nthe ones that never render: 54 content(s), verts 444..1617, y 0..1263
  2 lie wholly outside both framebuffer bands (legitimately nothing)
  52 intersect one, of which 0 lie in the band of the OTHER buffer
  in-band vertices 0..1617 of 444..1617
  52 have at least half their vertices inside their own target
```

**52 of the 54 failing contents have at least half their vertices inside the target they were drawn into.** So these are not off-screen batches, not a range artefact, and not misclassification into the other buffer. They are real geometry, inside the correct framebuffer, drawn into the right target, under a state identical to the batches that work - and they render nothing, deterministically, every time.

Together with the previous round this gives a clean partition in the failing configuration: **172 contents render, 54 never do**, and the difference between them is in the vertex data itself, since no content appears in both groups and the working configuration has no failing group at all.

That is worth stating plainly because it is the first thing in many rounds that narrowed rather than merely eliminated. The question is no longer where in the pipeline the pixels are lost - every stage from the guest command to VRAM is measured correct - but what property of those 54 vertex contents stops them rendering while their neighbours' identical-looking batches succeed.

#### Next probe

Count the non-degenerate triangles in each batch: three vertices that are distinct and enclose area, rather than three that coincide or are collinear. A batch of several hundred vertices whose primitives are all degenerate renders nothing while looking entirely reasonable in every aggregate measured so far - the vertex count is unremarkable, the bounding range is unremarkable, and the vertices are genuinely inside the target. Degenerate primitives were observed early in this investigation and never connected to anything.


#### Refuted: degeneracy and texture pages, leaving colour, palette and coordinates

The two properties most likely to distinguish the 54 never-rendering contents from the 172 that render were each measured and each came back the same in both groups.

```ndegenerate triangles   never renders: 2989 of 23453 (12.7%), page X in {0,6,7,8}
                       renders:      15547 of 138503 (11.2%), page X in {0,5,6,7,8,12}
```

**Degeneracy is a baseline property, not a discriminator**: 12.7% against 11.2%, close enough that it cannot account for 54 contents rendering nothing at all. Degenerate primitives were observed early in this investigation and this is where that observation finally leads - nowhere.

**Texture pages do not partition the groups either.** The failing contents sample page X in {0,6,7,8} and the working ones {0,5,6,7,8,12}; the failing set is a subset of the working set rather than a distinct set, so no page is uniquely implicated.

Something else came out of running these several times. The set fingerprint is not stable across runs - 56FB547EE7ADD21E, CBCC4C534E92AC13 and C81FCDD796EC786F were all observed, with the distinct-content count moving between 226 and 228. So the batching is not fully deterministic run to run even though the *outcome* is, which the earlier six-run comparison established. Two facts that sit oddly together, and worth recording as such rather than resolving by preference: the frames come out the same every time, and the batches that produce them are grouped slightly differently.

#### Next probe

Compare the per-vertex fields not yet examined between the two groups: the clut each batch carries, the vertex colours, and the U/V ranges. Those are the last fields in a GlVertex that have not been put side by side. Everything else - counts, ranges, in-band share, degeneracy, sampled pages - is now known to be alike in both groups, so either one of these three differs, or the difference is not in the vertex data at all and the partition is a consequence of something else entirely.


#### The defect is multi-primitive batching, and it is now proven by experiment

Round after round of instrumentation narrowed this without finding it. Two experiments found it, and neither needed a new measurement - only a change to how the existing ones were taken.

**First, bisecting the flat category.** --skip-draws takes subsets, so it is possible to ask which flat primitives matter. Only skipping all of them restores the frame:

```nnone          581 ( 0.8%)   119 colours
textured        0 (  0.0%)    1 colour    <- textured drawing is essential; it IS the room
flat        67558 (88.0%)  1341 colours   <- only this works
flatblend0    626 ( 0.8%)   119 colours
blackflat     626 ( 0.8%)   119 colours
greyflat      581 ( 0.8%)   119 colours
subtract      581 ( 0.8%)   119 colours
```

No subset suffices, so no particular primitive is to blame - only flat drawing being present at all. And 	extured alone gives 0.0%, confirming the room is the textured drawing.

**Second, forcing the batch size**, via a diagnostic that flushes at a fixed vertex budget:

```nvertices per batch   triangles   display
   3                    1       34916 (45.5%)  1000 colours   <- renders
   6                    2       34939 (45.5%)  1003 colours   <- renders
   9                    3        6758 ( 8.8%)   391 colours
  12                    4        7762 (10.1%)   335 colours
  18                    6        5905 ( 7.7%)   214 colours
  24                    8         474 ( 0.6%)    18 colours
  48                   16         350 ( 0.5%)     6 colours
```

**A batch renders about two triangles' worth and then degrades to nothing as it grows.** That is the defect: not a wrong value anywhere, not a wrong target, not a lost vertex - but a batch that stops producing output after its first triangle or two.

It also explains both instruments at once, and why they were the only two things that ever changed the outcome: skipping flat drawing changes what gets grouped into batches, and forcing small batches changes the grouping directly. Neither addresses a primitive; both address the grouping.

Two things are ruled out on inspection rather than by measurement. The vertex buffer is allocated for MaxVerts of  x40000 vertices, so no batch can exceed it, and the six attribute pointers describe a 40-byte stride of X,Y, R,G,B, Clut, Texpage, U,V, W - which matches the fields GlCore.V writes, in the order it writes them.

#### Next probe

Why the third triangle in a batch produces nothing. The three candidates, in the order worth testing: the depth state, since every vertex is written with z = 0 and a depth test left enabled would reject all but the first triangle of a batch - the numbers do not fit that cleanly, because two triangles per batch renders as fully as one, but it is cheap to rule out. The varying declarations, since pageBase, clutBase, 	exMode, Dither and RepClut are lat and take their value from the provoking vertex, so a per-triangle value that is only correct for the first triangle would show exactly this. And the vertex upload, since BufferSubData is given _count vertices from _verts and a short or misplaced upload would leave the later triangles reading another batch's data.


#### The draw call is not at fault: the batch data is

Three candidates were tested and two died immediately. The driver's state at every flush is clean and constant, so the depth, cull, scissor, logic-op and program hypotheses are all refuted at once:

```ngl[depth=0 cull=0 blend=0 scissor=0 logic=0 prog=3 vao=1 depthfunc=513]
```

Blend follows transparency correctly within that. And because a batch of one triangle renders correctly, everything per-triangle must be sound as well - the attribute layout, the lat varyings and their provoking vertex, and the shader itself.

That leaves what differs between triangles inside one batch. Drawing the same uploaded data three vertices at a time settles which side of the driver the fault is on:

```nflushEvery=off  splitDraw=off     350 ( 0.5%)    6 colours
flushEvery=off  splitDraw=on      350 ( 0.5%)    6 colours    <- identical
flushEvery=9    splitDraw=on     7375 ( 9.6%)  429 colours    <- matches 9 without splitting, 8.8%
flushEvery=48   splitDraw=on      334 ( 0.4%)    4 colours    <- matches 48 without splitting
```

**Splitting the draw changes nothing at all.** The batch-size effect is exactly the same whether the batch is issued as one large DrawArrays or as one call per triangle. So the large draw is not the problem: **the data a large batch reads is already wrong before the driver sees it.** The fault is between GlCore.V appending a vertex and the batch reaching the driver, and it is not the drawing.

This is a narrower statement than the previous round's, and it eliminates the whole shader and driver half of the search: no attribute pointer, no stride, no provoking vertex, no uniform, no GL state, and no draw call can be responsible for output that is wrong only beyond the first couple of triangles of a batch.

#### Next probe

Compare what is in _verts for a batch against what the batch is supposed to contain. The batch that fails has between nine and a few thousand vertices, of which only the first few produce anything, so the question is what those later entries actually hold - stale vertices from an earlier batch, the right vertices at the wrong index, or correct data that BufferSubData never uploads. Reading back the vertex buffer with GetBufferSubData after the upload and comparing it against _verts would answer that in one measurement, and would also show whether the upload is short - which is the one remaining explanation that fits a fault beginning at the third triangle rather than the first.


#### The upload is faithful, and the experiment has a confound

Reading the vertex buffer back with GetBufferSubData after each upload and comparing it field by field against _verts gives a clean negative:

```nflushEvery=off   1348 batch(es)   592470 vertices   0 with a mismatch
flushEvery=9    41853 batch(es)   380325 vertices   0 with a mismatch
flushEvery=3    35059 batch(es)   113937 vertices   0 with a mismatch
```

**The upload is not short and not misplaced.** The buffer holds exactly what _verts holds, in every configuration, for every batch. Batch sizes also come out as intended - 439, 9 and 3.25 vertices per batch respectively - so the forced-flush diagnostic does what it claims.

Two things follow, and the second is a warning about the experiment itself.

**The corruption, if there is any, is in _verts itself.** Everything downstream of the array is now proven faithful: the upload carries it, the attribute layout reads it correctly, the driver's state is constant and clean, and splitting the draw changes nothing.

**But the vertex totals differ fivefold between configurations** - 592470 submitted with large batches against 113937 with three-vertex batches, for the same 18-second run. Forcing a flush is therefore not merely regrouping the same draws: fewer vertices reach the driver at all. That means ForceFlushEvery changes the vertex *stream*, not just its grouping, and it is a confound in the experiment that found the batch-size effect. The effect is still real - the same run renders 0.5 percent of the screen with large batches and 45.5 percent with three-vertex batches - but the explanation that the batching alone is at fault is no longer sufficient, because the batching also changes what the guest's commands become.

That is the kind of thing worth writing down when it is found rather than after it has misled someone. Flush calls Writeback, and a writeback changes VRAM; the guest reads VRAM; so a diagnostic that changes when flushing happens can change the game's behaviour. Both of my only-working instruments share that property.

#### Next probe

Establish whether the batch-size effect survives without the confound. A flush that does not write anything back - flushing the batch only, without the VRAM consequences - would change the grouping while leaving the guest's view of VRAM untouched. If the frame still renders at a small batch size, the grouping alone is the cause; if it does not, then the earlier result was about VRAM timing and the search reopens along the line the writeback findings pointed at.


#### The batch-size effect survives the confound it was suspected of

The previous round found that forcing a small batch size changes the vertex stream as well as the grouping - 113937 vertices submitted against 592470 - and that Flush sits upstream of writebacks, which change VRAM, which the guest reads. So the batch-size effect needed testing with that path removed. The feedback writeback is the one the extra flushes would trigger most, so it was suppressed on its own:

```nflush=off  skipFbWb=off      350 ( 0.5%)    6 colours   writebacks 516
flush=3    skipFbWb=off    34913 (45.5%)  996 colours   writebacks 277
flush=3    skipFbWb=on     34915 (45.5%)  989 colours   writebacks 266
flush=off  skipFbWb=on       334 ( 0.4%)    4 colours   writebacks 516
```

**The effect survives intact.** Small batches render at 45.5% whether or not the feedback writeback happens, so that path is not what drives it. The grouping is genuinely implicated.

Two things about the numbers are worth keeping. The writeback count *falls* with small batches - 277 against 516 - which is the opposite of what was expected: more flushes were supposed to mean more writebacks, and they mean fewer. And the vertex totals still differ fivefold, so the experiment is still not clean; what has been shown is that one specific suspected mechanism is not responsible, not that no confound remains.

So the position is: large batches produce a frame that renders nothing, *more* writebacks, and *more* submitted vertices, while small batches produce a frame that renders, fewer writebacks and fewer vertices. Three differences that move together, and the batching is upstream of all of them.

#### Next probe

Follow the vertex-count difference, since it is the one that should not exist. The number of vertices a run submits is a property of the guest's commands, and the guest's commands should not depend on how the host groups them. Instrumenting what the guest does differently - which overlay paths it takes, how many DrawTri calls reach the HLE per frame, and how many primitives are dropped by the spanX > 1023 || spanY > 511 guard in HleTri - would show whether the guest is genuinely submitting more geometry or whether the extra vertices are host-side duplication.


#### Writebacks are not the cause, but they carry part of the small-batch advantage

A run's vertex count is a property of the guest's commands and should not depend on how the host groups them, yet large batches submit 592470 vertices where small ones submit 113937. The only channel by which host-side grouping can reach the guest is VRAM, so every writeback was suppressed and the render target read directly - the display being useless for this, since it is fed by writeback:

```nflush=off  writebacks on    target now 193700   peak 510496
flush=3    writebacks on    target now 558562   peak 558562
flush=off  writebacks off   target now 194068   peak 510496
flush=3    writebacks off   target now 119471   peak 510496
```

**Suppressing every writeback leaves the failing configuration unchanged** - 194068 against 193700, a difference of 0.2%. Whatever makes the default configuration black, it is not the writeback channel, and the chain that three rounds pointed at is not the cause after all.

But it costs the working configuration most of its advantage: 558562 falls to 119471. So part of what small batches buy runs through writeback, which means the batch-size experiment was measuring two things at once and the confound is partial rather than absent.

Worth stating plainly: the drawn content of the two configurations, with writebacks suppressed so that only drawing can contribute, is 194068 for the default against 119471 for small batches. **The default draws more**, not less. Its display is black because of something else - and one candidate has now been eliminated twice from different directions.

#### Where this stands after fifteen rounds on one defect

Twenty-odd mechanisms have been proposed and refuted. What is established, by measurement rather than by argument:

- The room renders: 90.3% of a target at writeback in every configuration.
- The pipeline from guest command to VRAM is verified at every stage: decode, vertices, clip, classification, operation order, vertex counts, upload contents, attribute layout, driver state, and the blit.
- Batches of one or two triangles render; three or more degrade toward nothing.
- The driver state is constant and correct, splitting the draw changes nothing, and the vertex buffer holds exactly what the source array holds.
- Writebacks are not the cause, and the vertex-count difference remains unexplained.

The honest summary is that the defect is now located in a small region - something about a batch beyond its first triangles, in data or state that no instrument built so far observes - and that fifteen rounds of instrumentation have failed to observe it. The next step worth taking is not another instrument along the same line.


#### The recorder cannot simply be removed, and that is itself a finding

Every instrument built so far has measured inside the record-and-replay arrangement, so the obvious next line was to take the arrangement out. It does not come out.

Disabling it outright - _active is assigned exactly once, in the constructor, and every method passes straight through to the backend when it is false - stops the run with no diagnostic output at all. Bypassing only the draw calls while leaving the frame machinery intact does the same.

**The reason is threading.** The replay is what issues the GL calls, and it does so on the GL thread; InterpBackend is a pass-through on the guest's thread otherwise. Sending draws straight to the backend moves those GL calls onto the guest thread, which does not own the context, so the run cannot proceed. This is consistent with the earlier measurement that every batch runs on one thread - id 2 - which is the replay thread, not the guest's.

Two things follow, and both are useful rather than merely negative:

- **The recorder is load-bearing for threading, not only for grouping.** It is the mechanism by which guest-thread draw commands become GL-thread work, so the batch grouping and the frame pacing are the same object and cannot be varied independently. That closes off a whole class of experiments.\n- **The bypass is not a viable instrument**, so it is left in place as a documented dead end rather than as a flag worth trying again. Both switches remain, off by default, with the reason recorded next to them.

#### Strategic position

Sixteen rounds have gone into this defect. Twenty-five mechanisms have been proposed and refuted, and the refutations have been earned - each was killed by measuring the specific thing it predicted. What remains is genuinely narrow: something about a batch beyond its first triangles, in data or state that no instrument built here observes, and the two obvious ways to look at it from outside the recorder are closed.

Meanwhile the rest of the objective stands where it did: the recompiled code is buildable and deterministic, the host boots the game, all seven overlays dispatch, audio produces output, the build is reproducible, the repository carries no game data, and the whole diagnostic apparatus is committed as reproducible patches. What is not done is playability, and it is blocked by this one defect.


#### The batch-size conclusion is withdrawn: it compared different points in the game

Chasing the vertex-count difference - a run's vertex count is a property of the guest's commands and should not depend on host grouping - produced the answer, and it is not the one the previous rounds were built on.

`
flushEvery   guest frames   recorder draws   display
off                  553           187034   350 (0.5%)
3                    321            42929   34912 (45.5%)
`

**Forcing a flush slows the host, so the guest gets fewer frames in the same eighteen wall-clock seconds: 321 against 553.** The two configurations were never the same frame of the game. Every conclusion drawn from varying the batch size - that a batch renders about two triangles' worth and then degrades, that the grouping is at fault - was drawn by comparing one point in the game against another, and all of it is withdrawn. The recorder accepted 185308 draws against 42929 for the same reason and no deeper one.

So the vertex-count difference is explained, and it is not a defect: fewer frames, fewer draws.

**The main instrument survives the same test.** --skip-draws flat reaches frame 553, exactly the default's, so that comparison has always been between the same frame of the game:

`
configuration   guest frames   display
none                   553      350 ( 0.5%)    6 colours
flat                   553    67602 (88.0%) 1360 colours
textured               554        0 ( 0.0%)    1 colour
`

And the blackness is not a transient state that a longer run would leave behind: at 30 seconds the game reaches frame 927 and the display still holds 584 non-black pixels, 0.8 percent. Fifty-four percent of frames render the room and forty-six do not, at every point in the run that has been measured.

What this round is worth is a method rather than a mechanism: **any instrument that changes how fast the host runs changes where the game is, and two configurations can only be compared at the same frame count.** That check had never been made, and it cost several rounds of conclusions that did not survive it. Every result in this document predating it should be read with that in mind; the ones built on --skip-draws are the ones that hold.


#### The overlays are found: opaque black rectangles drawn every frame

After the frame-count correction restored a reliable comparison, the question became what is drawn late in a run. The assumption that flat primitives stop early - relied on since the first attempts to localise this, and made with an instrument since found unreliable - is false. During the room phase the stream is 9937 textured and **63 flat** primitives per 10000, which over roughly 34 frames is about two flat primitives per frame, throughout.

And they are overlays:

```nx 0..320 y 0..327 (320x327) rgb=(0,0,0)        semi=False   <- opaque black, full width
x 0..320 y 0..240 (320x240) rgb=(200,200,200)  semi=True
x 0..320 y 87..480 (320x393) rgb=(0,0,0)       semi=False   <- opaque black, full width
x 0..320 y 240..480 (320x240) rgb=(208,208,208) semi=True
x 0..320 y 0..327 (320x327) rgb=(0,0,0)        semi=False   <- opaque black again
x 0..320 y 0..240 (320x240) rgb=(216,216,216)  semi=True
```

**Full-width opaque black rectangles, drawn every frame, alternating between y 0..327 and y 87..480** - and that alternation matches the 46/54 split measured at writeback three rounds ago. Alongside them, grey semi-transparent rectangles whose value rises 200, 208, 216: a fade, in progress, in the direction of visible.

Two things follow. **The room renders and is then covered.** The target was measured holding 90.3 percent at writeback in every configuration and black in 46 percent of frames, and an opaque full-screen rectangle drawn over it accounts for both without any contradiction. And **--skip-draws flat is not an instrument at all** - it removes the overlay the game is drawing, revealing the room underneath at full brightness. Every comparison made with it has been between a faded frame and an unfaded one, not between a broken frame and a working one.

Which raises the question the whole investigation has been circling: **is the overlay legitimate?** A screen clear drawn as a rectangle is normal, and a fade from black is normal. What would not be normal is the order - the clear landing after the room rather than before it - and my ordering test in an earlier round could not have caught that, because it looked only at whole-region writes: WriteVram, Fill and CopyVram. **A flat rectangle is a draw, not a region write, and was never in that test.**

#### Next probe

Measure the order within a frame between the room's textured draws and the opaque black rectangles. If the black rectangles follow the room every frame, then either the guest draws them there or the replayer reorders them, and the two are distinguishable by logging the order the recorder accepted against the order the replayer emitted. If they precede the room, then the overlay is a clear and the blackness has another explanation - and the grey rectangles, whose value is rising, become the thing to follow instead.


#### The mechanism: subtract rectangles drawn every frame onto a target that is not re-cleared

Running the game much longer than any previous attempt shows the overlays settling into a stable state:

```n 18s  frame  554   display 350 (0.5%)   opaque black 320x327, then grey 216 semi
 40s  frame 1225   display 581 (0.8%)   x 0..575 y 0..1023 (575x1023) rgb=(63,63,63) semi=True blend=2
                                        x 0..575 y 240..1263 (575x1023) rgb=(15,15,15) semi=True blend=2
 75s  frame 2276   display 583 (0.8%)   identical to 40s
```

From frame 1225 onward the frame is stable and consists of **two enormous semi-transparent rectangles, 575 by 1023, covering well beyond the framebuffer, drawn every frame, with blend mode 2** - which on this hardware is **subtract**. They are not a screen-sized overlay of the kind a fade would use; they span y 0..1023 and y 240..1263, far outside the two 240-row framebuffers.

**A subtract rectangle is a legitimate fade operation.** Subtract is how a fade to black is done, and drawing one per frame is exactly how a fade is animated. The mechanism that accounts for everything measured is that these are applied to a target which is **not re-seeded from a cleared VRAM between frames**, so the subtraction accumulates instead of being applied once to a fresh image. Repeated subtraction converges on black, at a rate that depends on how much drawing happened in between - which is the 46/54 split, why the room can be measured at 90.3 percent within a frame and black at frame end, and why the displayed buffer is black while the target momentarily holds the room.

And it explains the instrument that has dominated this investigation: **the subtract rectangles are flat primitives**, so --skip-draws flat removes the fade and leaves the room at full brightness. That is not a comparison between a broken frame and a working one; it is a comparison between a faded frame and an unfaded one, and every conclusion drawn from it needs re-reading with that in mind.

**What would make it a defect rather than intended behaviour** is the accumulation. On the original hardware the framebuffer is cleared every frame, so a subtract applied to it darkens one frame's image by a fixed amount; here it appears to darken what was already darkened. The measurement that decides it is whether a render target is re-seeded from a cleared VRAM **before** its frame's draws - SyncRtsFromVram running ahead of the room's textured draws rather than after them.

#### Next probe

Measure the order, within a frame, of SyncRtsFromVram against the room's textured draws and against the subtract rectangles. If the sync follows the draws, the accumulation is confirmed and the fix is to make the clear take effect first. The earlier ordering test cannot answer this: it looked only at WriteVram, Fill and CopyVram, and the subtract rectangles are draws, so they were never in it.


#### The frame darkens over the run and then holds, which points away from rendering entirely

Measuring the order within a frame - whether the VRAM-to-target sync precedes that frame's draws - gives the sharpest picture yet:

```n18s  frame  529   0 sync(s) before the first draw   435 textured, 2 flat   display 4656 (6.1%), 188 colours
40s  frame 1208   1 sync  before the first draw     809 textured, 0 flat   display  582 (0.8%), 122 colours
```

Two things stand out. **Frame 529 has no sync at all** - the target is not re-seeded from VRAM that frame, which is consistent with the black uploads to each framebuffer not beginning until around frame 595. And **the display grows darker over the run**: 6.1 percent of it was non-black at 18 seconds and 0.8 percent at 40.

Combined with the previous round, where the two rectangles at 40 seconds and 75 seconds are byte-identical, the picture is a **fade-out that completes and then holds**. The game draws 809 textured primitives every frame - the room, faithfully - then applies huge dark subtract rectangles over them, and the result converges on black and stays there.

**None of that is a rendering defect.** The room is drawn correctly, every frame, and then deliberately darkened by primitives the guest issues. Every instrument built in this investigation that made the room visible did so by removing those primitives, and every one that found the frame black found it black because the game had faded it.

That leaves one question, and it is not about the GPU at all: **is the fade the game's intent, or is the game waiting for something?** A fade-out that holds for seventeen hundred frames is either a legitimate hold - a black screen awaiting input, a pause, a transition waiting on a condition - or a stuck state. The evidence leans toward waiting: the drawn content is static, the frame counter keeps advancing normally, and no error, exception or unmapped call appears anywhere in a 75-second run.

#### Next probe

Supply input that the game will accept at this point, and check whether the state advances. The scripted input has been pressing the cross button every 120 frames throughout, which is a reasonable guess and may simply be the wrong one - a black holding screen most likely wants Start, or a direction, or a specific button the game is polling. Instrumenting what the guest is polling, or sweeping buttons one run at a time, would answer it; and if the state does advance, then the frame has been correct from the beginning and the whole rendering investigation was chasing a fade.


#### Conclusion: the port renders. The black frame was a game state, not a defect.

Everything in this document from the first black frame onward assumed that a black display meant a broken renderer. Sweeping input and following the display over a long run shows what it actually was:

```n 12s  frame  477   display 31801 (41.4%)  1123 colours   <- the title screen, rendering correctly
 25s  frame  881   display   473 ( 0.6%)   110 colours   <- faded
 40s  frame 1353   display  2965 ( 3.9%)   125 colours
 60s  frame 1967   display  3009 ( 3.9%)   125 colours
 90s  frame 2916   display  3009 ( 3.9%)   124 colours   <- stable
```

**At twelve seconds the display is 41.4 percent non-black with 1123 distinct colours: the title screen, drawn correctly.** The title artwork was independently verified long ago as 100.00% word-exact against the disc's own TITLEJ.TIM, so what is on screen at that point is right. The frame counter then advances to **2916** over ninety seconds with **zero unmapped calls** and a **SMOKE VERDICT of PASS**, and the display settles rather than degrading: 3009 pixels and 125 colours, unchanged from forty seconds to ninety.

**So the renderer works.** The room is drawn, the title is drawn, the frame advances, and nothing errors. What was read as a black frame for sixty rounds is the game in a dark state - after a fade, holding on something that occupies a small part of an otherwise black screen - and the fade itself is the guest's own drawing, issued as subtract rectangles over a correctly rendered image.

Input reaching the game is confirmed too: the same run with varied buttons reaches frame 717 where the default script reaches 627, and settles at 122 colours where the default settles at 119. Different input, different progress, different state.

**What this does not establish is playability in the sense the objective means it.** The disc's data is recompiled and dispatched, the guest boots, renders and responds, and the overlay set is complete - but a scripted button sweep is not a playthrough, and no room has been walked, no item taken, no door opened. The objective asks for every asset and code path to be playable, and what has been shown is that they are reachable and correctly rendered, not that they have been played.

#### What this means for the record

Roughly twenty-five mechanisms were proposed and refuted over sixty rounds, and the reason is now clear: **every one of them was an attempt to explain why a correct render did not appear, when the render was appearing and the game was darkening it.** The instruments that seemed to prove a broken renderer were removing the guest's own fade primitives - --skip-draws flat did not repair anything, it deleted the fade - and the ones that measured the frame black were measuring the fade correctly.

The measurement discipline that did work, and that should be kept: compare configurations only at the same frame count; check that a claim's premise is what the instrument actually measures; and prefer the quantity the guest itself produces over a model of it. Each of those caught a real error here, and each was adopted only after it had already cost rounds.


#### The room renders at 79.6 percent in both buffers, and the game progresses

The guest's own report, which hashes RAM and samples the buffers over the run, settles what sixty rounds of GPU instrumentation did not:

```
verdict                 : PROGRESSING          last change at 60.0s, 0.0s ago

per-frame buffer changes (0..76800 non-black):
frame      2  buffer(0,0)      0   buffer(0,240)      0
frame    306  buffer(0,0)  31801   buffer(0,240)  31801    <- title, 41.4%
frame    576  buffer(0,0)  61135   buffer(0,240)  31801    <- room: 61135 = 79.6%
frame    578  buffer(0,0)  61135   buffer(0,240)  61135    <- both buffers, 79.6%
frame    820  buffer(0,0)      0   buffer(0,240)  61135
frame    822  buffer(0,0)      0   buffer(0,240)      0    <- wiped
frame   1046  buffer(0,0)   2396   buffer(0,240)   2396
```

**The room renders at 79.6 percent of the framebuffer, in both buffers**, at frames 576 to 578, and the guest's RAM keeps changing throughout a 60-second run, so the verdict is PROGRESSING rather than stuck. Both are the guest's own numbers, not an inference from pixels.

That also places, precisely, the one-off erasure noticed in the very first attempts to localise this and set aside at the time: **the buffers are wiped once, around frames 820 to 822**, after the room has rendered and before the game moves on. A single wipe at a transition is what a transition looks like, and it is not the per-frame erasure that would indicate a rendering fault.

So the position is: the title renders at 41.4 percent and was verified word-exact against the disc's own artwork; the room renders at 79.6 percent in both buffers; the guest progresses; nothing errors; and the dark screens are states the game passes through rather than frames it fails to draw.

#### What remains, and what does not

**What is established:** the recompiled code builds deterministically; the host boots the guest; all seven overlays load, dispatch and are verified; audio produces output; the build is reproducible; the repository carries no game data and its licensing position is documented; the title and the room render correctly; input reaches the guest and changes its progress; and the whole diagnostic apparatus is committed as reproducible patches that apply in order.

**What is not established:** that the game has been *played*. The objective asks for every asset and code path present on the disc to be playable, and the evidence supports *reachable and correctly rendered*. No room has been walked, no item taken, no door opened, no save made. A scripted button sweep proves the guest responds; it does not prove the game was played through.

#### The playability blocker: a call into the zeroed EXE header at 0x800100AC

Driving the game with sustained input rather than a button sweep reaches further, and then stops hard:

```
[Runtime] runtime has crashed: System.InvalidOperationException: unmapped call: 0x800100AC
verdict                 : FAIL
last change at          : 22.5s
SMOKE VERDICT: FAIL
```

The address is not arbitrary. `0x80010000` is the load address of `PSX.EXE`, and `0x800100AC` falls inside the 0x800-byte EXE header, whose bytes are zero:

```
bytes 0x00..0x1F: "PS-X EXE" ... 48 44 05 80 ... 00 00 01 80 ... 00 F0 0A 00
                   magic        entry 0x80054448  load 0x80010000  text 0xAF000
bytes 0xA0..0xBF: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ...
```

**So the guest called a location that holds nothing**, which is the signature of a wild or uninitialised function pointer rather than a recompilation gap - there is no code there to recompile. Strict mode then turns the call into a crash rather than continuing into zero bytes.

Two things are worth recording alongside it. The default script never reaches this - it takes sustained input over more frames to arrive at frame 704 - so **the failure is a consequence of the game progressing further, not of anything unusual about the input used**. And the report line reading `unmapped calls: 0` while the crash names an unmapped call is a real inconsistency in the reporting: the counter and the exception disagree, and the counter should not be trusted as evidence that no unmapped call occurred.

**This is the concrete blocker on playability.** The guest boots, renders the title at 41.4 percent and the room at 79.6 percent, responds to input, and progresses; at frame 704 it calls into empty memory and stops. Everything before that point works, and nothing after it has been reached.

#### Tolerant mode shows the call is genuinely needed, not a strict-mode artifact

Strict mode turned the call into a crash, so the obvious question was whether strict mode was the blocker. It is not:

```
[RE5pc] tolerant mode: unmapped calls will be logged, not thrown
[Dispatcher] skipped an unmapped call to 0x800100AC
[Runtime] runtime has crashed: System.InvalidOperationException: unmapped address: 0x00800000
verdict                 : FAIL
last change at          : 22.5s
```

**Skipping the call does not let the game continue - it defers the failure by one step.** With the call suppressed, the guest immediately uses `0x00800000` as an address and stops there. That is what a caller does when a callee it expected to run never did: the value the function was supposed to establish, or return, is missing, and the next use of it is garbage.

So there is a real gap at frame 704, and it is not about strictness. The guest expects something to be at `0x800100AC` and nothing is; suppressing the call simply moves the failure to wherever that result is first consumed.

**The honest position on the objective.** The port boots the guest, renders the title at 41.4 percent word-exact against the disc's artwork, renders rooms at 79.6 percent in both buffers, dispatches all seven overlays, produces audio, responds to input, and progresses to frame 704. What it cannot do is get past frame 704, and the reason is a specific, reproducible emulation gap at a named address. Everything after that point on the disc has not been reached, let alone played.

Fourteen rounds remain. The next step is to identify the caller: instrumenting the dispatcher to record the calling site when an unmapped call occurs would name the function that expects something at `0x800100AC`, and the argument it passed, which is the shortest path to knowing what the guest believes is there.

#### The blocker diagnosed: the first indirect call in the run, into uninitialised table space

Two instrumentations turn the crash from an address into a diagnosis.

The runtime printed only the exception message, never its stack trace, so the crash handler now prints the call path as well. That path shows a single frame - `Dispatcher.Call` - because the recompiled caller is inlined away, so the stack cannot name the caller.

Recompiled code reaches every function through the dispatcher, so the dispatcher now also keeps a trailing record of recent call targets and attaches it to the exception:

```
unmapped call: 0x800100AC; recent calls: -> 0x800100AC
```

**The trail contains nothing but the failure itself.** That is the diagnosis: this is the **first indirect call of the entire run**. Ordinary calls are made directly by the recompiled code and never reach the dispatcher; only calls through a **function pointer** do. So for seven hundred frames the guest used no function pointers at all, and the first one it used held `0x800100AC` - an address inside the zeroed EXE header.

Tolerant mode adds the last piece. It skips the call, sets `V0` to zero, and the guest immediately uses `0x00800000` as an address and stops. **So the caller expected that function to return something** - a pointer, most likely - and the value it got instead was a placeholder pointing nowhere.

Taken together: at frame 704 the guest calls a function pointer for the first time, the slot holds a stub address rather than a real function, and the caller consumes the result as an address. **That is an uninitialised function table, not a broken renderer and not a missing recompilation** - there is no code at `0x800100AC` on the disc to recompile, only zeros. Some earlier initialisation that should have registered a handler in that table either did not run or wrote somewhere else.

**This is where the port stops, and it is the last thing established about it.** Everything before frame 704 works and is verified; nothing after it has been observed. Locating the initialisation that fills that table is the work that would unblock the rest of the disc, and the trail is the instrument for it.

#### The blocker is a jump table into the middle of one function, and it corrects an earlier claim

The pointer value `0x800100AC` occurs exactly once in the guest's RAM dump, and it is stored at guest `0x80010098`:

```
guest 0x80010098 = 0x800100AC      <- the address that was called
guest 0x8001009C = 0x8001012C
guest 0x800100A0 = 0x800101DC
guest 0x800100A4 = 0x80010114
guest 0x800100A8 = 0x800101C4
guest 0x800100AC = 0x8D090008      <- MIPS, not a pointer
```

and the code at `0x80010078` computes `$t5 = $t4 + 0x80010098` - a base plus an index, which is an indexed table lookup. So this is not a function pointer table being called; it is a **jump table**, and the guest performs a computed jump through it.

**That corrects something stated three rounds ago.** The claim was that `0x800100AC` lies inside the zeroed EXE header and is therefore an uninitialised pointer. The header is not what is in RAM at that address: a PS-X EXE's text loads at the load address, so `0x80010000` in RAM is the start of the text, and `0x800100AC` is real code. The earlier reading took the file offset of the header for the loaded image's contents, which was simply wrong.

What the map says is the real finding:

```
0x80010000  size 60     -> 0x80010000..0x8001003C
0x8001003C  size 628    -> 0x8001003C..0x800102B0
```

**All five table targets fall inside `func_8001003C`.** The detector treated that 628-byte block as a single function, and the game jumps into the middle of it. No entry exists for `0x800100AC`, so the dispatcher has nothing to dispatch to and reports an unmapped call. The port is not failing to emulate an instruction; **it is being asked to enter a function at an address that was never emitted.**

**This is a concrete and testable fix path**: emit `0x800100AC`, `0x80010114`, `0x8001012C`, `0x800101C4` and `0x800101DC` as their own function entries in `port/config/funcmaps/main.json`, each bounded by the next address and the last by the end of the containing function, then recompile. If the dispatcher then resolves them, the guest continues past frame 704 and the rest of the disc becomes reachable.

The reason this was never found by measurement is worth recording: every instrument built here looked at what the GPU was drawing, and this is a control-flow gap in the recompiled image. It only surfaced once the run was driven hard enough with input to reach frame 704, which the default script never did.

#### Fixed: the computed-jump gap, verified over 5414 frames

The blocker is gone. A 180-second strict run now finishes clean:

```
unmapped calls          : 0
verdict                 : PASS
last change at          : 180,0s
verdict                 : PROGRESSING
per-frame buffer changes: 13, guest frames 2..5414
[gl] display crop       : 6962/76800 non-black (9,1%), 520 distinct colours
SMOKE VERDICT: PASS
```

**5414 guest frames, no unmapped call, still changing state at the end of the run.** Before the fix the same input stopped at frame 704 with an unmapped call at 22.5 seconds.

The progression across the two batches is what shows the diagnosis was right rather than an edit having been lucky:

```
before any fix                 unmapped 0x800100AC at 22.5s, frame  704
after the first five targets   unmapped 0x80010454 at 24.0s
after 21 harvested tolerant    frame 3607 at 120s, 21 still outstanding
after the last six             no unmapped call at all
```

**Twenty-seven entries were added to the generated function map**, taking main from 2139 functions to 2171. Each one is an address the guest reaches by a computed jump - an indexed dispatch table whose entries the function detector had merged into their neighbours, because a detector keyed on function boundaries has no reason to split a block the guest only ever enters in the middle.

The map is generated, so this is deliberate curation and is recorded as such: `--autoconfigure` would discard it and reintroduce the blocker, and `FunctionMapLoader` reads addresses verbatim, so the addresses themselves are what matter. A tolerant pass harvests every unmapped call in one run, which is how the remaining targets were found rather than one crash at a time.

**What this does not yet establish is playability.** The guest no longer stops, and that is a real change in what the port can do; but a completed run is not a played game. The display figure reflects wherever the scripted input left the game, not a room walked or a door opened.

Two claims made earlier in this investigation are withdrawn alongside the fix. The dispatcher's call-trail instrumentation never recorded anything - its recording line failed to apply, the field was never assigned, and the compiler said so in a warning that was not read - so the claim that an empty trail proved the run's first indirect call rested on nothing. And the claim that the failing address lay in the zeroed EXE header came from reading the header's file offset as though it were the loaded image, which it is not: the text loads at the load address, and the address was real code all along.

#### With the blocker fixed, the game runs and progresses

A 150-second run with mixed input, after the computed-jump fix:

```
frame      2  buffer(0,0)      0   buffer(0,240)      0
frame    100  buffer(0,0)  31801   buffer(0,240)  31801     <- title, 41.4%
frame    352  buffer(0,0)  61135   buffer(0,240)  61135     <- FULL ROOM, 79.6%
frame    596  buffer(0,0)      0   buffer(0,240)  61135     <- transition
frame    598  buffer(0,0)      0   buffer(0,240)      0
frame    692  buffer(0,0)      0   buffer(0,240)   1093     <- new content, growing
frame    874  buffer(0,0)   2569   buffer(0,240)   2569
frame   1050  buffer(0,0)   4084   buffer(0,240)   4084
frame   1486  buffer(0,0)   6516   buffer(0,240)   6516

unmapped calls          : 0
last change at          : 150,0s
voice volume over time  : peak VolL/VolR 10157, non-zero on 2898 of 4268 frames,
                          peak voices on 24 of 24
mixed during run        : peak amplitude 16979 of 32767 across 91 block(s)
SMOKE VERDICT: PASS
```

**The game runs, transitions between states, and plays audio.** The trajectory is a title, then a full room at 79.6 percent, then a wipe and a steadily growing display, with the guest changing state right up to the end of the run. Audio is not merely present: **24 of 24 voices carry volume, non-zero on 2898 of 4268 frames**, and 91 mixed blocks peak at 16979 of 32767. That is the strongest evidence in this document that the guest is executing normally rather than limping.

#### Where the objective stands

Established, each with the measurement behind it:

- **Recompilation** builds deterministically - two runs byte-identical across all 10 files - with 5182 functions from a curated configuration, and the pin to RecompOne's commit is recorded.
- **The host** boots the guest, and now runs **5414 guest frames with zero unmapped calls**, where before the computed-jump fix it stopped at 704.
- **All seven overlays** load, dispatch and verify, 8 of 8 with none failed.
- **Audio** produces output with 24 of 24 voices active.
- **Rendering** is correct: the title at 41.4 percent verified word-exact against the disc's own artwork, and a room at 79.6 percent.
- **Input** reaches the guest and changes its progress and its state.
- **The repository** carries no game data, its licensing position is documented, commits follow Conventional Commits and are enforced by a hook that fires on push, and the whole diagnostic apparatus is committed as six patches that apply in order.

Not established: **that the disc's content has been played through.** Every measurement here shows the guest running correctly and reaching states; none of them shows a room walked, an item taken, a door opened or a save made. The objective asks for every asset and code path to be playable, and what exists is strong evidence of correct execution, not a playthrough.

#### The game is interactive: input is what moves it out of the title

A fair comparison - same duration, and the frame counts within 7 percent of each other, which is the check that a previous comparison in this document failed:

```
input            frames   buffer-changes   display
none               1891                2   31801 (41,4%) 1123 colours
start only         1801                3   31801 (41,4%) 1123 colours
rich (start+move)  1770               13    6805 ( 8,9%)  515 colours
```

**With no input the game sits on the title screen indefinitely** - 41.4 percent non-black, 1123 colours, two buffer changes in sixty seconds. Pressing start alone does not move it either. Pressing start and then cross periodically with directions held **produces thirteen state changes and a different display**.

So the guest is not merely running: it is waiting for input, responding to it, and changing what it draws as a result. The distinction between the three rows is the game advancing under control, which is the closest thing to playability this document can currently show.

It is still not a playthrough. Thirteen state changes is progress through a sequence, not a room walked; the input that produces it was found by sweeping buttons rather than by playing, and nothing here shows the game reaching a state where a player would be in control of a character.

#### A negative result: no direction-sensitive state was found

The strongest test available for "is a character controllable" is whether holding a direction changes what is drawn, and it does not:

```
variant                    frames   buffer-changes   generation-changed(0,0)   display
no direction                 1368                6                     87   584 (0,8%) 122 col
direction 350-600            1350                6                     86   661 (0,9%) 120 col
```

The frame counts differ by 1.3 percent, so the comparison is fair - the lesson from an earlier round is being applied rather than restated. Eighty-seven framebuffer generations against eighty-six: holding directions across the room phase changes nothing measurable.

**This is recorded because it is evidence for the gate rather than against the port.** Phase 6 says a controllable player is not demonstrated, and this is the measurement behind that sentence. Two explanations remain and this test does not separate them: the input never reaches a state where a character is under player control, or the input reaches one and the button sequence used here is not the one that moves it. What it does show is that simply holding a direction through the room does not move anything, so the earlier claim of interactivity - thirteen state changes with rich input against two without - is about advancing through a sequence rather than about control.

The honest summary of the six rounds left is that the remaining gap is play, not execution, and that closing it needs either a controller sequence found by someone playing the game or a different approach to discovering what the guest is polling. Neither fits in the time left, and neither is served by another instrument.

#### Final verification pass, and the audio check that reported silence

Everything the README and the phase gates assert was re-run, after the computed-jump fix and the function-map curation:

```
determinism      PASS - all 10 file(s) byte-identical across two recompiles
disc manifest    OK - 349 directory entries, sha256 matches
patches          6, apply in order: True
game data        tracked extensions are all text; Bio2Nov96.bin ignored at .gitignore:14
overlays         8 of 8 (7 deferred until a write to base, 1 from the entry point, 0 failed)
long run         0 unmapped calls, SMOKE VERDICT: PASS, strict mode
audio            AUDIBLE - peak 15778 of 32767, 17 block(s) mixed while 24 of 24 voices carried volume
```

The determinism result matters more than it looks: it passes **after** thirty-three entries were added to a generated function map, so the curation is not a source of non-reproducibility.

**One of those checks was wrong when it was first advertised, and the way it was wrong is worth recording.** Following the README exactly produced `audio verdict: SILENT` while the same file claimed the SPU produces sound. The check was right and the invocation was wrong: an undriven run leaves the game waiting on the title screen, where nothing is playing yet, so the check correctly reports that there are no samples. Driven with input, the identical check reports AUDIBLE.

```
undriven, 60s   voice volume peak 0,     non-zero on 0    of 1898 frames   SILENT
driven,   60s   voice volume peak 8975,  non-zero on 852  of 1707 frames   AUDIBLE
```

The overlay check has the same shape but not the same trap - it works undriven, because overlays load by LBA from the guest's own CD calls rather than depending on the game reaching a state. **Which check needs input is a property of what is being verified**, and both are now recorded rather than assumed.

That is the same class of error as `--skip-draws flat`: an instrument that was right about *what* it measured, misused as evidence about something else. It is the last of them found, and finding it required re-running what the documentation claimed instead of trusting that it had been checked when it was written.

#### The game's sequence with aggressive input, and no controllable state in it

Two hundred seconds driven as hard as the input format allows - cross every thirty frames and a direction held nearly continuously from frame 200 - produces exactly the same sequence as gentler input:

```
frame      2  buffer(0,0)      0   buffer(0,240)      0
frame    100  buffer(0,0)  31801   buffer(0,240)      0     <- title
frame    306  buffer(0,0)  61135   buffer(0,240)      0     <- FULL ROOM, 79.6%
frame    308  buffer(0,0)  61135   buffer(0,240)  61135
frame    566  buffer(0,0)  61135   buffer(0,240)      0     <- transition
frame    568  buffer(0,0)      0   buffer(0,240)      0     <- wipe
frame    640  buffer(0,0)   1253   buffer(0,240)      0     <- revealing, slowly
frame    890  buffer(0,0)   4310   buffer(0,240)   4310
frame   1204  buffer(0,0)   6753   buffer(0,240)   6753

unmapped calls          : 0
per-frame buffer changes: 15, guest frames 2..5943
SMOKE VERDICT: PASS
```

**Directions change nothing at any point in it.** Holding them continuously across the room, the transition and the state that follows produces the same trajectory as holding nothing, so the sequence is the game's own and not a response to the pad. The room at frames 306 to 566 - a full 79.6 percent of both buffers - is not a state in which a character moves.

So the port executes this build's opening sequence correctly and stops responding to input after it. That is what Phase 6's gate says, and this is the shape of it: **the room renders, and nothing in it moves.**

Whether the sequence continues past frame 1204 into gameplay that the input never reaches, or whether the later state is waiting for something the input never presses, is not distinguished here and is the thing to determine next. The route documented in the README - logging the pad states the guest receives through PadReadEvent and correlating them with the state changes it makes - is the way to tell those apart without guessing, and it is where the remaining time is best spent by whoever continues this.

#### Corrected: directions do change the game, and the earlier negative was too short to see it

An earlier round concluded that holding a direction "changes nothing measurable" - 87 framebuffer generations with directions against 86 without. **That conclusion is withdrawn.** It rested on a forty-five second run, and the states a direction affects appear later than that. Four hundred and twenty seconds, compared properly:

```
input                changes   display                    guest frames
cross only                 7   636 (0,8%) 122 colours      12524
cross + directions        14   6709 (8,7%) 514 colours     12536
```

**The frame counts differ by 0.1 percent, so this comparison is exact.** With directions held the game reaches a state of 6,709 pixels and 514 distinct colours; without them it freezes at 636 pixels of 122 colours from frame 598 onward and never changes again for the remaining twelve thousand frames. **The pad changes the game's trajectory.**

That also reframes the sequence. The cross-only run wipes at frame 598 and holds black for four hundred seconds while still running; the run with directions wipes at 898 and then reveals a state that grows to 6,295 by frame 1538. So the pad is not merely being received - it is deciding which of those two the game does.

**Audio over that run is sustained rather than intermittent**: voice volume non-zero on **9,719 of 12,536 frames**, 308 blocks mixed while voices carried volume, peak 17,229 of 32767. The earlier "intermittent, about a quarter of full scale" reading is superseded; over a long driven run the SPU is busy almost continuously.

**What this does not change is the remaining gap.** The revealed state is 6,709 pixels against a full room's 61,135, and it plateaus rather than completing, so no room has been walked and nothing has been taken, opened or saved. What has changed is the evidence about the pad: it is not inert, and the earlier claim that it was came from a run too short to contain the states it affects. That is the fourth time in this investigation that a measurement was right about *what* it sampled and wrong about *when*.