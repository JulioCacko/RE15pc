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
4. Audio. Nothing has been proven to play at all.
5. Recompile twice and confirm `generated/` is byte-identical, which is what decides whether
   it may stay ignored.
