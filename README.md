# RE15pc

A native PC port of the **Biohazard 1.5 (November 6, 1996) prototype** — the
abandoned Resident Evil 2 build commonly known as *Resident Evil 1.5* — produced
by statically recompiling the original PlayStation executable rather than
emulating it.

> **Status: it boots, runs and dispatches, but does not yet draw the game.**
>
> Working: the disc is identified and validated; the recompiled executable runs real
> game code at 30 fps with zero unmapped calls; scripted controller input drives it
> through the title and character select into **STAGE1**; **all seven overlays
> dispatch correctly**; the **audio path is measured to produce output**; and the
> title screen's image is proven resident in video memory at a **100.00% word-exact**
> match.
>
> Blocked: the composed frame comes out black. Every component between a guest GP0
> command and the framebuffer has been measured and cleared - geometry, texture
> addressing, sampled data, the blend chain, the modulation colour, the drawing
> offset, draw order, the render target lifecycle and the interpolation recorder -
> and the room's drawn output still never reaches video memory. The room renders
> correctly at 88% non-black with `--skip-draws flat`, which is a diagnostic and not
> a workaround.
>
> See [docs/phases.md](docs/phases.md) for the phase gates and
> [docs/boot-status.md](docs/boot-status.md) for the evidence, including the
> mechanisms that were proposed and then withdrawn after measurement.

---

## You must supply your own disc

This repository ships **no game data**. It contains tooling, configuration and
documentation only.

To build and run RE15pc you need your own copy of the prototype disc image,
placed in the repository root as `Bio2Nov96.bin` with `Bio2Nov96.cue` beside it.
The image is excluded by `.gitignore`: at 124.3 MB it exceeds GitHub's per-file
limit, and it is copyrighted Capcom material that must not be redistributed.

The port refuses to start against a disc it does not recognise, so a wrong or
corrupted image fails immediately with a readable message instead of producing
garbage output.

## What it is

**RE15pc is a recompilation toolchain and host application, not a game.** What this
repository holds is: a recompiler configuration, function maps, a host program that
runs the recompiled result, and the diagnostics used to work out what that result is
doing. What it does not hold is any part of the game.

Static recompilation translates the PlayStation's MIPS instructions into C#
ahead of time, then runs that code on top of a runtime that models the PS1
hardware in software. No BIOS or other Sony component is needed — only the game
disc. Compared to emulation this trades a one-off translation pass for much
lower per-frame overhead, which is what makes a native port of this build
practical.

The translation happens **on your machine, from your disc, at build time**. The
recompiled code is never produced here and never distributed here; it lands in
`generated/`, which `.gitignore` excludes. The same applies to the port's
diagnostic dumps of video memory, RAM and frame captures, which contain decoded
game art and are excluded under `/out/` for exactly that reason.

The engine is [RecompOne](https://github.com/BlackLabelHQ/RecompOne) by
BlackLabelHQ (MIT). RE15pc pins it at a known commit, keeps it out of this
repository, and records any needed change as a patch in [`patches/`](patches/).

## This repository contains no game data

Every tracked file is plain text — source, configuration and documentation — and
that is checkable rather than merely promised:

```powershell
git ls-files | ForEach-Object { [IO.Path]::GetExtension($_) } | Sort-Object -Unique
git check-ignore -v Bio2Nov96.bin generated/ out/ RecompOne/
```

The first command lists every extension present: `.cs .csproj .cue .editorconfig
.gitattributes .gitignore .json .md .patch .ps1 .sh .sln .yml`. The second shows
the disc image, the recompiled output, the diagnostic dumps and the upstream engine
all excluded by named rules in `.gitignore`.

**If you are a rights holder:** this project has no connection to Capcom and
contains no Capcom material of any kind. If you believe something here is
nevertheless a problem, please open an issue or contact the repository owner — the
default position is to remove anything objected to rather than to argue about it.
See [NOTICE](NOTICE) for the full statement.

"Resident Evil", "Biohazard" and related names are trademarks of Capcom Co., Ltd.
This is an unofficial, non-commercial preservation and research effort, not
affiliated with or endorsed by Capcom. Nothing in the documentation is legal advice.

## Building

Prerequisites: [.NET SDK 10](https://dotnet.microsoft.com/download) and Git.

```powershell
pwsh -File bootstrap.ps1
```

That single command activates the commit-message hook, clones RecompOne at its
pinned commit, applies `patches/`, and builds. It is idempotent; add `-Force` to
throw away the RecompOne clone and start from a clean upstream tree.

Then run the port:

```powershell
dotnet run --project port/RE15pc -- --cue .\Bio2Nov96.cue
```

## Verifying a build

Every claim above is reproducible, and each of these runs the check rather than
asserting the result.

```powershell
# the recompiler is a pure function of the disc and the config: recompiles and
# compares, failing if any output byte differs
pwsh -File tools/Test-RecompileDeterminism.ps1

# all seven overlays dispatch, without needing gameplay to reach them
# (prints 8 of 8, 0 failed; writes out/diagnostics/overlay-dispatch.txt)
dotnet run --project port/RE15pc -- --cue .\Bio2Nov96.cue --smoke 25 --verify-overlays

# the SPU produces sound: mixes a block while voices carry volume and peaks near 26%
# (writes out/diagnostics/audio.txt)
dotnet run --project port/RE15pc -- --cue .\Bio2Nov96.cue --smoke 25 --verify-audio

# the disc is the one this port expects, checked by hash and by file layout
pwsh -File tools/New-DiscManifest.ps1 -Check

# commit messages follow Conventional Commits v1.0.0
sh ci/check-commits.sh HEAD~5..HEAD
```

The commit check can also be run as the actual GitHub Actions workflow, locally and
without GitHub, using `act` against a container engine. Both the passing and the
failing outcome have been verified that way — see
[CONTRIBUTING.md](CONTRIBUTING.md#running-the-workflow-itself-locally).

Two further options exist for investigating the rendering blocker, and are described
in [docs/boot-status.md](docs/boot-status.md): `--skip-draws <class>` suppresses a
class of primitive (`all`, `textured`, `flat`, plus colour and blend variants), and
`--software-gpu` disables the GPU HLE. **Neither is a workaround.** `--skip-draws
flat` renders the room at 88%, but it also removes the title and select screens'
flat drawing, so it is an instrument and not something to play the game with.

## Layout

| Path | What it is |
|---|---|
| `port/config/` | Recompiler configuration: which files are code, where overlays load |
| `port/RE15pc/` | The host application that boots the recompiled game |
| `patches/` | Diffs against pinned upstream RecompOne, applied by `bootstrap.ps1` |
| `generated/` | Recompiler output. Not committed; regenerated from disc + config |
| `disc-manifest.json` | Hash and file table of the disc this port targets |
| `docs/` | Phase gates, compatibility notes, investigation writeups |
| `RecompOne/` | Upstream working clone. Not committed; restored by `bootstrap.ps1` |

## Commits

Every commit follows [Conventional Commits
v1.0.0](https://www.conventionalcommits.org/en/v1.0.0/), enforced by a tracked
hook in `.githooks/`. See [CONTRIBUTING.md](CONTRIBUTING.md) for the type and
scope vocabulary.

## Scope

This project targets **PC (Windows/Linux) via .NET**. It does not target, and
the C# runtime cannot produce, builds for consoles such as the Nintendo 64,
Saturn, 3DO or Jaguar. Reaching those would require a separate backend emitting
portable C plus per-console hardware layers.

"Complete" here means every asset and code path **present on this disc** is
reachable. The November 1996 build is an unreleased, unfinished prototype:
content that was never authored into it cannot be restored, and this project
does not invent it. What the build contains is documented in
[docs/compatibility.md](docs/compatibility.md) rather than assumed.

## Legal

See [NOTICE](NOTICE). No Capcom code, art, audio or disc data is redistributed.
*Resident Evil* and *Biohazard* are trademarks of Capcom Co., Ltd. This is an
unofficial, non-commercial preservation and research effort, not affiliated with
or endorsed by Capcom.
