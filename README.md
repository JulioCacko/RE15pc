<h1 align="center">RE15pc</h1>

<p align="center">
  <img src="assets/logo.png" width="460" alt="The Biohazard 1.5 wordmark in red on black" />
</p>

<p align="center">
  <b>A native Windows x64 preservation port of the Biohazard&nbsp;2 November&nbsp;6,&nbsp;1996 prototype — Resident&nbsp;Evil&nbsp;1.5 — built by statically recompiling the disc's MIPS executable and its seven overlays.</b>
</p>

<p align="center">
  <a href="#quick-start">Quick start</a> ·
  <a href="#build-the-executable">Build the exe</a> ·
  <a href="#product-tour">Screens</a> ·
  <a href="#what-works-today">Status</a> ·
  <a href="#controllers">Controllers</a> ·
  <a href="#resolution-and-display">Resolution</a> ·
  <a href="#run-it-like-an-installed-game-not-an-emulator">No disc needed</a> ·
  <a href="#verification">Verification</a> ·
  <a href="docs/phases.md">Phases</a> ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

<p align="center">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%20x64-0078D6?style=flat&logo=windows&logoColor=white" />
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet&logoColor=white" />
  <a href="LICENSE"><img alt="license" src="https://img.shields.io/badge/license-MIT-blue.svg?style=flat" /></a>
  <a href="docs/phases.md"><img alt="phase" src="https://img.shields.io/badge/phase-2%20of%205%20passed-yellow?style=flat" /></a>
  <img alt="checks" src="https://img.shields.io/badge/focused%20checks-166%20passing-green?style=flat" />
  <a href="CONTRIBUTING.md"><img alt="commits" src="https://img.shields.io/badge/commits-Conventional%20Commits%201.0.0-FE5196?style=flat" /></a>
  <a href="https://github.com/BlackLabelHQ/RecompOne"><img alt="recompiler" src="https://img.shields.io/badge/recompiler-RecompOne-d81dec8-lightgrey?style=flat" /></a>
</p>

<p align="center">
  <img src="assets/social-preview.png" width="100%" alt="Key art: the Biohazard 1.5 wordmark above the Resident Evil 1.5 title with the cracked eye of the prototype's logo" />
</p>

> [!IMPORTANT]
> **You must supply your own disc.** This repository carries no Capcom code, audio or disc image, and none of the recompiled output derived from the disc is distributed here. The disc is identified by hash in `disc-manifest.json`; it is not included and never will be. The only game-derived imagery committed here is the small set of screenshots in this README, listed by name in [NOTICE](NOTICE). This is an unofficial, non-commercial preservation project, not affiliated with or endorsed by Capcom.

---

## What this is

*Resident Evil 1.5* is the version of Resident Evil 2 that Capcom scrapped in early 1997, mid-development. The November 6, 1996 build survives as a disc image, and for decades the only way to see it was to emulate it.

**RE15pc runs it natively.** The disc's MIPS executable and its seven overlays are statically recompiled to C# by [RecompOne](https://github.com/BlackLabelHQ/RecompOne), and a Windows host boots the result on the real GPU. No emulator, no BIOS, no interpreter — the guest code becomes native code that a .NET runtime executes directly.

The project is built around one rule: **fix what the port breaks, and invent nothing.** Where the prototype itself is incomplete — and as an unfinished build, it often is — that incompleteness is preserved rather than papered over. Every status claim below traces to a recorded run with `run.json`, `verdict.txt` and an exit code; the tools that produce them are in `tools/`, and you can re-run all of them.

| | |
|---|---|
| **Disc** | Biohazard 2, November 6, 1996 prototype (Resident Evil 1.5) |
| **Recompiler** | RecompOne, pinned at `d81dec8c9622fdcd0865d73588a3baa8d3c3a605` |
| **Recompiled output** | ~98 MB of C# across 5150 generated functions and 7 overlays |
| **Target** | Windows x64, strict preservation. Deterministic rebuilds across 10 generated files. |
| **Disc coverage** | 334-file ledger: 40 partially observed, 294 unverified |

---

## Product tour

Every frame below is a real 320×240 framebuffer readback written by this port's own diagnostics while running the actual disc — not a mock-up, and not a screenshot of an emulator. Each one comes from a run whose recorded verdict is `PASS`.

<table>
<tr>
<td width="50%" valign="top">
<img src="docs/screenshots/title.png" width="320" alt="The BIOHAZARD 2 title screen: red logo over the cracked eye, with NEW GAME, LOAD DATA and CONFIG listed beneath" /><br/>
<sub><b>Boot to title</b> — the November 6, 1996 build reaches its own title menu from a cold start, artwork and all. 320×240, frame 1650.</sub>
</td>
<td width="50%" valign="top">
<img src="docs/screenshots/character-select.png" width="320" alt="The PLEASE SELECT MAIN CAST screen showing Leon S. Kennedy and Elza Walker with their profiles" /><br/>
<sub><b>Character select</b> — <code>PLEASE SELECT MAIN CAST</code>, with both scenarios: Leon S. Kennedy and <b>Elza Walker</b>, the protagonist who never shipped. 320×240, frame 1200.</sub>
</td>
</tr>
<tr>
<td width="50%" valign="top">
<img src="docs/screenshots/rooftop.png" width="320" alt="Leon standing on the rooftop helipad with the railing and the H mark painted on the platform below" /><br/>
<sub><b>The first playable room</b> — Leon on the rooftop. Rendered background, working collision against the railing, and a deliberate exit through the door into room 116. 320×240, frame 2700.</sub>
</td>
<td width="50%" valign="top">
<img src="docs/screenshots/inventory.png" width="320" alt="Elza's inventory screen showing ARMS CONTROL and ITEM LIST panels, with an ECG readout reading Fine" /><br/>
<sub><b>Inventory</b> — <code>ARMS CONTROL</code> and <code>ITEM LIST</code>, opened and closed with state preserved, item selection working, and a weapon-equipment change verified. 320×240, frame 2500.</sub>
</td>
</tr>
<tr>
<td width="50%" valign="top">
<img src="docs/screenshots/combat.png" width="320" alt="Elza in a blood-stained corridor facing two figures through a barred shutter" /><br/>
<sub><b>Actors and damage</b> — Elza facing two original actors through a barred shutter. A separate 4000-frame gate verifies within-run damage and their persistent removal, distinguishing health resets from respawns. 320×240, frame 4000.</sub>
</td>
<td width="50%" valign="top">

<p align="center"><img src="assets/banner.jpg" width="320" alt="The Resident Evil 1.5 key art: red title text beside the cracked eye" /></p>

<sub><b>Key art</b> — the project's identity art, also composed into the social card above with <code>tools/New-SocialPreview.ps1</code>.</sub>
</td>
</tr>
</table>

---

## What works today

Honest status, split by what has actually been demonstrated under a recorded gate versus what has not.

### Verified

| Area | Evidence |
|---|---|
| **Cold boot to title** | 180-frame native gate, PASS, exact frame counts, both positive cases byte-identical |
| **Both player entries** | Leon's first-room gate (room 117 → 116) and Elza's entry to `ROOM1031.RDT` both pass |
| **Movement and collision** | Turning, world movement, railing collision with sliding, and one deliberate door transition — paired against an idle control run |
| **Rendering** | Rooftop and room backgrounds render; the earlier missing-background defect traced to synchronous GPU readback and repaired |
| **Inventory** | Open/close with state preserved, item selection, a weapon-equipment change, pistol reload and the ammunition menu |
| **Combat** | An aimed two-shot firing gate passes on ammunition and control; a 4000-frame gate verifies damage and persistent actor removal |
| **Audio** | Non-zero queued PCM inspected passively — the mixer is never advanced to manufacture a sample |
| **Overlays** | All 49 ordered overlay replacements exercised |
| **Determinism** | Recompilation reproduces 10 generated files byte-for-byte |
| **Controllers** | Xbox pads enumerated through the XInput backend; button and axis polling verified — see [Controllers](#controllers) |

### Not yet verified

| Area | Status |
|---|---|
| Full room and scenario coverage | Open — 294 of 334 disc files remain unverified |
| Remaining interactions and enemy behaviour | Open — one encounter and two actors are covered, not the game |
| Save / load persistence | Open |
| Full native-input certification | Open — known synthetic arrow-event gaps fail acceptance rather than being swallowed |
| Stability gate | Open — representative 30-minute sessions not yet run |

The phase table in [`docs/phases.md`](docs/phases.md) is the authoritative version of this; **phase 2 of 5 has passed locally**, and gold is not close. Synthetic dispatch success, nonzero audio, pixel counts and a clean smoke run deliberately do **not** count as playability here.

---

## Requirements

| | |
|---|---|
| OS | Windows x64 |
| To **run** a published build | Nothing else — the runtime is bundled |
| To **build** from source | Git, PowerShell 7, .NET SDK 10 |
| Your own disc | `Bio2Nov96.bin` (124.3 MB) with `Bio2Nov96.cue` beside it |

> [!NOTE]
> GitHub Actions is currently blocked from starting by account billing limits, so a local pass is not a remote check pass. This is stated rather than hidden because the commit-convention workflow is real enforcement, and it is not running.

---

## Quick start

```powershell
# 1. Restore the toolchain: commit hook, pinned RecompOne clone, tracked patches, build
pwsh -File bootstrap.ps1

# 2. Recompile the disc into C# (writes generated/, ~98 MB).
#    The config carries the cue path and the output directory internally.
dotnet run --project RecompOne/RecompOne.Recompiler -- port/config/bio2nov96.json

# 3. Play
dotnet run --project port/RE15pc -c Release -- --cue .\Bio2Nov96.cue
```

`bootstrap.ps1` pins RecompOne at `d81dec8c…` and applies the 12 tracked patches in `patches/`. It is idempotent.

> [!WARNING]
> Do **not** regenerate the curated function maps with `--autoconfigure`. That discards recovered computed-jump targets and silently breaks overlay dispatch.

---

## Build the executable

**You do not have to build anything.** [Releases](https://github.com/JulioCacko/RE15pc/releases) attach a Windows x64 build that carries its own .NET runtime — unzip it, put your `Bio2Nov96.bin` and `Bio2Nov96.cue` next to `RE15pc.exe`, and run it. The archive never contains the disc.

To produce one yourself:

```powershell
pwsh -File tools/Publish-Exe.ps1          # self-contained build into dist/
pwsh -File tools/New-ReleasePackage.ps1   # republish and zip into release/
```

`Publish-Exe.ps1` writes a self-contained Windows x64 build to `dist/`, carrying the runtime, the RecompOne runtime, the compiled guest and every native dependency. It also copies your disc, cue sheet, `settings.json` and `interface.ini` in, so `dist/` is directly runnable, and it verifies afterwards that the icon resource landed and that `cimgui.dll`, `glfw3.dll`, `SDL2.dll` and `soft_oal.dll` are actually present.

`New-ReleasePackage.ps1` republishes from source and then packages it, **excluding the disc image by name** so a release cannot silently start shipping the game alongside the code. It writes a `README.txt` explaining that the player must supply the disc, which is the one thing a downloader has to know.

### Why a folder and not one file

`-SingleFile` exists but is **not** the default, because two dependencies of this host fail under single-file packing — and they fail at runtime, not at build time:

- **ImGui.NET** locates `cimgui.dll` through `Assembly.Location`, which is empty for an assembly inside a single-file bundle. The host then calls into an unloaded native library and dies with `0xC0000005`. It surfaces late — in `HostWindow.OnClosing` → `ConfigManager.SaveView` → `igSaveIniSettingsToMemory` — so **the game runs and the process only crashes on the way out.** A single-file build looks healthy right up until it corrupts its own exit.
- **MonoMod.RuntimeDetour**, which RecompOne uses for mod hooks, documents single-file as unsupported, and the SDK emits an explicit warning saying so.

Both were reproduced against SDL 2.30.8 / net10.0: `PublishSingleFile=true` aborts with `0xC0000005`; `PublishSingleFile=false` completes and passes the 180-frame gate. Trimming stays off for the same class of reason — the guest is reached through dispatch tables, so a trimmer would delete code that looks unreferenced and break dispatch at runtime.

---

## Controllers

**Xbox controllers work through SDL's XInput backend**, and that is a choice the host makes explicitly rather than something it gets by accident.

`InputManager.Initialize` sets `SDL_JOYSTICK_RAWINPUT=0` before initialising SDL's game-controller subsystem. That disables SDL's RawInput backend, which is what pushes XInput-capable pads onto the **XInput driver** instead. The effect is directly observable in the device path SDL reports:

| `SDL_JOYSTICK_RAWINPUT` | Device path SDL reports | Driver that took the pad |
|---|---|---|
| `0` — what the runtime sets | `XInput#0` | **XInput** |
| `1` — SDL's default | `\\?\HID#VID_045E&PID_028E&IG_00#…` | RawInput / HID |

Verify it on your own machine, with your own pad:

```powershell
pwsh -File tools/Test-GamepadBackend.ps1 -VirtualPad
```

The tool answers three separate questions instead of asserting one: it scans the bundled `SDL2.dll` for the XInput backend markers (`SDL_XINPUT_ENABLED` and the built-in `xinput,*,a:b0,…` mapping, both present in SDL 2.30.8), reproduces the runtime's exact hint and init sequence and reports which driver won, and exercises button-plus-axis polling. The polling check attaches an SDL **virtual** controller, so it proves the data path with nothing plugged in — and it is ordered before any physical pad is touched, because polling a real pad first stops a subsequently attached virtual one from reporting state at all (reproduced directly; an SDL virtual-device quirk, not a port defect).

Bluetooth and non-XInput pads fall back to SDL's HIDAPI backend, which is also compiled in. Two pads are supported, in digital or analog mode, with per-device binding selection and rumble.

---

## Resolution and display

The port does not assume a resolution. Window geometry is **derived from the monitor** at launch instead of hard-coded, so the same build behaves sensibly on a 1024×600 netbook, an ultrawide, and a 4K panel at 150% scaling.

| How you launch it | What you get |
|---|---|
| Default, and no size saved yet | A window at **90% of the monitor's work area**, centred |
| `--resolution 1920x1080` | Exactly that, or scaled down if the screen cannot hold it |
| `--window-mode borderless` | Fills the monitor's work area, no title bar, **no resolution change** |
| `--window-mode fullscreen` | Exclusive fullscreen, driven by the driver |
| `--window-mode windowed` | A normal resizable window |

**Borderless is the mode that works everywhere**, because it never asks the driver to change display mode — it cannot pick a mode the monitor does not support, and it leaves the desktop compositor in charge so alt-tab, overlays and multi-monitor keep working. Exclusive fullscreen is still there for anyone who wants it, and the three modes are switchable from **Settings → Display** without restarting.

The game image is then **aspect-fitted** into whatever space the Output panel occupies, so 4:3 content letterboxes correctly inside 16:9, ultrawide or a pivoted portrait display rather than stretching. The guest's native framebuffer is scaled internally by **Render Scale** (Settings → Display, 1×–8×), independent of the window size.

### What was verified

Measured on a 3840×2160 panel at 150% scaling, work area 3840×2088:

| Case | Result |
|---|---|
| First run, no saved settings | 3456×1879, centred at (181,59), on-screen, layout correct |
| `--resolution 800x600` | Exactly 800×600 |
| `--resolution 4000x3000` | Clamped to 2560×1920 — **on-screen instead of hanging off the right and bottom edges** |
| `--window-mode borderless` | 3840×2088 at (0,0), no title bar, whole frame usable |
| `--resolution 19200x1080` | Refused with exit code 2 and an explanatory message, not silently clamped |
| Window resize, 700×560 → 2560×1440 and back | Identical output at identical sizes; the frame is a pure function of window size |

The old fixed 1280×720 window opened **partly off-screen on any display narrower than that**, and a window whose title bar is off the desktop cannot be dragged back. That is the failure this replaces.

> [!NOTE]
> Geometry is resolved *before* the window is created, not resized afterwards. That ordering is load-bearing: the ImGui dock layout is built against the viewport that exists when the first frame runs, so a window that grows after that keeps a layout sized for the old one and leaves most of itself as bare dockspace background. This was hit, diagnosed and fixed during development, and the reason is recorded next to the code.

---

## Run it like an installed game, not an emulator

The port does not need a disc image at runtime. Convert your disc **once**, and from then on it boots from a native data file with no `.cue`, `.bin` or `.chd` anywhere:

```powershell
pwsh -File tools/Import-Disc.ps1 -Disc .\Bio2Nov96.cue
```

That writes `data/re15pc.disc` — a single compressed file holding the disc's track table and its raw 2352-byte sectors. The port finds it automatically, so afterwards you just run `RE15pc.exe`:

```
dist/RE15pc.exe --frames 180        # boots from data/, no disc image needed
```

`data/re15pc.disc` is **41 MB against a 124 MB disc**, and there is no CD emulation left: no container parsing, no cue sheet to point at, no image to keep on disk. A normal game folder, with its data beside the executable. `--data <path>` overrides the location, and `--cue` still wins if you name a disc explicitly.

### Why the store keeps raw sectors

The CD layer is addressed in raw 2352-byte sectors, because that is how the guest streams XA audio and MDEC video — `ReadRawSector` has to return the sync, header, subheader and EDC/ECC bytes exactly as they sit on the disc. A store built from extracted ISO9660 files cannot work: the bytes outside each file's data area would have to be reconstructed, and nothing in a file listing records them. So the container changes and the data does not.

### The import proves itself

Every import re-opens the store it just wrote and compares **every raw sector and every track entry** against the source disc:

```
[RE15pc] verified: all 52,849 raw sectors and every track entry match the disc
```

The store also records the SHA-256 of the sector stream it was built from, so validation afterwards is stronger than the cue path, not weaker: an imported installation proves it came from the right disc without the disc being present, where a `.cue` build only checks the hash when the image is still on disk. A store built from a different image is refused with a message saying so.

> [!IMPORTANT]
> `data/` contains the game and is gitignored for that reason. The import is a one-way conversion — the store cannot be turned back into a disc image — and it is for your own use. Keep your disc if you want to re-import.

### The equivalence claim, stated honestly

The store is **byte-exact at the data level**: all 52,849 raw sectors and all track entries match. That is a stronger statement than "the game boots", and it is the one worth making, because the port is **not** run-to-run deterministic in guest RAM — two runs of the *same* disc already differ in their RAM and VRAM dumps, so an end-to-end comparison cannot distinguish a store defect from that noise. What the store guarantees is that the guest receives the same sectors, byte for byte. A 2100-frame scripted run reaching STAGE1 was also measured against both sources and produced byte-identical framebuffer readbacks.

---

## Verification

Nothing here is asserted without a command that can fail.

```powershell
# Focused checks, then the native acceptance harness with its negative controls
dotnet build tests/RE15pc.Checks/RE15pc.Checks.csproj -c Release
pwsh -File tools/Test-Acceptance.ps1

# Gameplay gates
pwsh -File tools/Test-FirstRoom.ps1        # Leon: room 117 -> 116
pwsh -File tools/Test-ElzaEntry.ps1        # Elza: entry to ROOM1031.RDT
pwsh -File tools/Test-WeaponFire.ps1
pwsh -File tools/Test-EnemyDamage.ps1
pwsh -File tools/Test-InventoryMenu.ps1
pwsh -File tools/Test-InventoryEquip.ps1

# Integrity
pwsh -File tools/Test-RecompileDeterminism.ps1   # 10 files, byte-identical
pwsh -File tools/New-DiscManifest.ps1 -Check     # your disc matches the manifest
pwsh -File tools/Test-ContentInventory.ps1

# Controllers
pwsh -File tools/Test-GamepadBackend.ps1 -VirtualPad
```

The acceptance harness deliberately runs **negative controls** alongside the positive ones — a silent-audio case, a timeout case and a refused non-empty output directory — and fails if any of them passes. A suite that can only agree with itself proves nothing.

For a scripted investigation run:

```powershell
dotnet run --project port/RE15pc -c Release --no-build -- --frames 2100 --timeout 120 --input "90:start,240:cross,300:cross,600:up:600,1250:cross,1800:up:240" --trace-input --verify-audio
```

`frame:buttons:duration` holds buttons for that many frames; an omitted duration means 12, and `none` releases them. Bounded runs write to a unique directory under `out/runs`, copy your settings and memory cards in to protect player state, and record `run.json`, `verdict.txt` and a process exit code.

> [!NOTE]
> A **bounded** run (`--frames` or `--smoke`) refuses to start outside a repository checkout, because acceptance evidence has to name its revision and configuration. An interactive launch has no such duty and proceeds, recording `unknown` provenance instead. This is why the published exe plays when double-clicked but declines a gate run from the wrong directory — with an explanation rather than a stack trace.

---

## Repository layout

```
port/RE15pc/          the host: options, disc identity, overlay policy, diagnostics
port/config/          recompiler configuration and the curated function maps
patches/              12 tracked diffs against the pinned RecompOne commit
tools/                gates, generators and diagnostics (all PowerShell)
tests/RE15pc.Checks/  the 166 focused checks
docs/                 acceptance evidence, phase gates, per-subsystem findings
assets/               wordmark, key art, app icon, social card
docs/screenshots/     the framebuffer captures shown above
data/                 imported disc store — the game's own sectors, never committed
bootstrap.ps1         restores the pinned toolchain, idempotently
generated/            recompiled C# — built locally from your disc, never committed
dist/                 published build — never committed
release/              release archive staging — never committed
```

---

## Repository rules

All commits follow [Conventional Commits v1.0.0](https://www.conventionalcommits.org/en/v1.0.0/), enforced by a tracked `commit-msg` hook and by the workflow in [CONTRIBUTING.md](CONTRIBUTING.md).

**Never commit the disc, `generated/`, `out/`, `dist/`, `release/`, saves, or the separate `RecompOne/` clone.** Recompiled code and game-derived binaries are produced on your machine from your own disc; `dist/` in particular is a ~154 MB folder with a transcription of the disc's executable compiled into it (plus your own disc image, if it copies one in). Runtime and recompiler changes are delivered as patches against the pinned upstream, never as edits to generated game code.

Published releases are the one place a game-derived binary is distributed, and only reachable because the player supplies the disc the code was generated from. That exception is written down in [NOTICE](NOTICE) rather than left implicit.

---

## Further reading

| Document | Contents |
|---|---|
| [`docs/acceptance.md`](docs/acceptance.md) | What the acceptance harness proves, and the limits of that proof |
| [`docs/phases.md`](docs/phases.md) | The five phase gates and where the project actually stands |
| [`docs/first-room.md`](docs/first-room.md) | Leon's first-room gate, in detail |
| [`docs/weapon-fire.md`](docs/weapon-fire.md) | The firing and damage gates |
| [`docs/content-coverage.md`](docs/content-coverage.md) | The 334-file disc ledger and its 294 unverified entries |
| [`docs/model-inventory.md`](docs/model-inventory.md) | 102 model blocks across all 54 EMS/PLD/PLW files |
| [`docs/background-readback.md`](docs/background-readback.md) | The synchronous GPU readback defect and its repair |
| [`docs/keyboard-input.md`](docs/keyboard-input.md) | Native input evidence and its known gaps |
| [`docs/compatibility.md`](docs/compatibility.md) | Disc identity, upstream pin, and what "complete" means for an unfinished build |
| [`docs/boot-status.md`](docs/boot-status.md) | Historical investigation log — earlier interpretations are not current claims |

---

## Licence

This repository's own contents — scripts, configuration, documentation and the image assets under `assets/` and `docs/screenshots/` — are MIT licensed; see [LICENSE](LICENSE) and [NOTICE](NOTICE). RecompOne is MIT licensed, © flaffy, and is fetched at a pinned commit rather than vendored. No licence is asserted over anything derived from the disc.

No Capcom game code, art, audio, disc image or Sony BIOS is redistributed. *Resident Evil*, *Biohazard* and related names and characters are trademarks of Capcom Co., Ltd.

<p align="center"><sub>An unofficial, non-commercial preservation and research effort.<br/>Built because the build exists, and someone should be able to play it.</sub></p>
