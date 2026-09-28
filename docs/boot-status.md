# Boot status

Where the port actually is, what has been proven, and what is known to be wrong.
Everything below is backed by an artifact on disk or a command that can be re-run.

Last updated at the end of Phase 5.

---

## Summary

The recompiled prototype **boots and runs**. Guest code executes, receives interrupts,
runs its threaded main loop, loads the `title` overlay, uploads the Japanese title
image to video memory and drives the display. There are **zero unmapped calls**.

One concrete rendering defect is identified and quantified below: the title image is
uploaded to VRAM **including its 20-byte TIM header**, which displaces the picture
10 pixels horizontally.

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

1. **Feed the game input.** This is now the blocking item, not the displacement. The guest
   is sitting on a menu waiting for a pad, and an unattended run can never provide one, so
   nothing past this screen is reachable by `--smoke` as it stands. A scripted input driver
   that presses Start and Cross on a schedule is the difference between watching one screen
   and walking the game forward, and it is a prerequisite for every later phase.
2. Settle the owner of the 10-pixel displacement once something else is progressing: find
   the guest TIM loader and see what it hands to the transfer. The section above records
   where to look.
3. Audio. Nothing has been proven to play at all.
4. Recompile twice and confirm `generated/` is byte-identical, which is what decides whether
   it may stay ignored.
