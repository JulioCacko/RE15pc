# RE15pc

A native PC port of the **Biohazard 2 (November 6, 1996) prototype** — the
abandoned Resident Evil 2 build commonly known as *Resident Evil 1.5* — produced
by statically recompiling the original PlayStation executable rather than
emulating it.

> **Status: early.** The repository is being bootstrapped. See
> [docs/phases.md](docs/phases.md) for the current gate and what is proven
> working.

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

Static recompilation translates the PlayStation's MIPS instructions into C#
ahead of time, then runs that code on top of a runtime that models the PS1
hardware in software. No BIOS or other Sony component is needed — only the game
disc. Compared to emulation this trades a one-off translation pass for much
lower per-frame overhead, which is what makes a native port of this build
practical.

The engine is [RecompOne](https://github.com/BlackLabelHQ/RecompOne) by
BlackLabelHQ (MIT). RE15pc pins it at a known commit, keeps it out of this
repository, and records any needed change as a patch in [`patches/`](patches/).

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
