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

### Why this is not yet attributed

The upload is width-320, so the runtime honoured the transfer rectangle. The source bytes
started at the file base. Whether that is the guest computing its pixel pointer without
skipping the header, or the game deliberately staging the whole file in VRAM before a
GPU-to-GPU blit that had not happened yet when the dump was taken, is **not settled**. Both
would produce these exact bytes. Deciding between them means reading the guest's TIM loader
in `generated/main.cs` or `generated/title.cs` and finding what it passes to the GPU.

Do not "fix" this by patching VRAM. Establish which side is wrong first.

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
`ram-overlay-80100000.bin`.

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

1. Identify the owner of the 10-pixel displacement: read the guest TIM loader and find
   what it hands to the GPU.
2. Reach the next boot milestone past character select. The `title` overlay's string table
   contains `Selecth3.tim`, `Selecth.tim`, `PLAYER PLD`, `POS X = %d`, `POS Y = %d`, so the
   screen in memory is the character select, and it has already advanced its menu index -
   `0x801026C4` holds `0x02` in RAM against `0x00` in the file, written by
   `func_80102038` in `generated/title.cs`.
3. Audio. Nothing has been proven to play yet.
