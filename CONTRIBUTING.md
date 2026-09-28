# Contributing to RE15pc

## Commit messages: Conventional Commits v1.0.0

Every commit in this repository follows
[Conventional Commits v1.0.0](https://www.conventionalcommits.org/en/v1.0.0/).
This is **enforced by a tracked hook**, not left to habit.

The hook lives at `.githooks/commit-msg` and is activated by `bootstrap.ps1`,
which runs:

```sh
git config core.hooksPath .githooks
```

Run that once per clone if you set the repo up by hand. To confirm it is active:

```sh
git config core.hooksPath     # -> .githooks
```

### Message format

```
<type>[optional scope][!]: <description>

[optional body]

[optional footer(s)]
```

- `feat` correlates with a MINOR version, `fix` with a PATCH, and a
  `BREAKING CHANGE:` footer (or `!` before the colon) with a MAJOR version.
- Types are case-insensitive per the spec, but this project writes them
  lowercase.
- `BREAKING CHANGE` must be **uppercase**. The hook rejects other casings.
- The description follows the colon and a single space.

### Types in use

`feat` · `fix` · `build` · `chore` · `ci` · `docs` · `style` · `refactor` ·
`perf` · `test` · `revert`

### Scopes in use

Scopes are a fixed vocabulary so history stays greppable. Use lowercase and
pick the narrowest one that fits.

| Scope | Covers |
|---|---|
| `disc` | cue sheet, `disc-manifest.json`, disc image hygiene |
| `config` | `port/config/*.json`, `funcmaps/*` |
| `recompiler` | `patches/` entries touching `RecompOne.Recompiler` |
| `runtime` | `patches/` entries touching `RecompOne.Runtime` |
| `host` | `port/RE15pc` (the host application) |
| `overlay` | overlay dispatch and `OverlayPolicy` |
| `cd` | ISO9660, `DiscFs`, `LibCd`, `LibDs` |
| `gpu` `spu` `mdec` `gte` `pad` `save` | per-subsystem HLE fixes |
| `gameplay` | game-logic-adjacent HLE work |
| `generated` | recompiler output and its determinism |
| `build` | solution, projects, CI |
| `repo` | repository meta (gitignore, hooks, bootstrap) |
| `docs` | documentation only |

### Rules the hook enforces

1. Header matches `<type>[(scope)][!]: <description>`.
2. Header length is at most 100 characters.
3. The description is not capitalised and does not end with a period.
4. `BREAKING CHANGE`, if present, is uppercase.
5. `Merge`, `Revert`, `fixup!` and `squash!` headers bypass the check, since git
   generates those itself.

### Examples

```
feat(host): add RE15pc host application
fix(disc): correct cue sheet track mode to MODE2/2352
chore(config): prune false-positive overlays from probe results
fix(spu): honour sequence loop points when reading BGM banks
docs: record playable-through status and known gaps

feat(recompiler)!: pin overlay base instead of inferring it

BREAKING CHANGE: overlay base must now be declared explicitly in the config,
because inference produced wrong regions for same-base overlays.
Phase: 5
```

### Optional footer

`Phase: <n>` records which plan phase a commit belongs to. It is a legal
Conventional Commits footer and keeps history phase-aware.

### Bypassing

`git commit --no-verify` skips the hook. Reserve it for genuinely exempt cases;
the point of the hook is that `git log` stays machine-readable.

### Enforcement in CI

The hook only runs where it is installed, and it cannot see a commit made through the
GitHub web editor — this history already contains one such commit. So
`.github/workflows/commits.yml` runs `ci/check-commits.sh` on every push and pull request,
validating only the commits being introduced so pre-existing history is not re-litigated
each time.

Run the same check locally:

```sh
sh ci/check-commits.sh HEAD~5..HEAD
sh ci/check-commits.sh            # validates HEAD only
```

It falls back to checking the tip when the range is unusable, which is what happens on a
first push (an all-zero "before" sha) or after a force push.

#### Running the workflow itself, locally

The whole job can be run on this machine without GitHub, using
[`act`](https://nektosact.com/) against a container engine. On Windows, Podman works and
needs no `DOCKER_HOST`, because it forwards a Docker-compatible API to the default pipe:

```powershell
winget install RedHat.Podman
podman machine init
podman machine start          # reports: API forwarding listening on npipe:////./pipe/docker_engine

# a payload standing in for a real push, so the range is exercised rather than fallen back
$before = git rev-parse HEAD~1; $after = git rev-parse HEAD
@{ ref='refs/heads/main'; before=$before; after=$after
   head_commit=@{ id=$after }; repository=@{ full_name='JulioCacko/RE15pc'; default_branch='main' }
} | ConvertTo-Json -Depth 5 | Set-Content .git/act-push.json

act push -W .github/workflows/commits.yml -e .git/act-push.json `
         -P ubuntu-latest=catthehacker/ubuntu:act-latest
```

Pinning `-P` avoids `act`'s interactive prompt for an image size. It also keeps the run
off the default micro image, which lacks the tools `actions/checkout` needs.

Both outcomes have been verified this way, not just the passing one:

- a one-commit push range checks one commit and reports **Job succeeded**;
- the whole-history range reports **`not conventional : e05b0ff`** and **Job failed**,
  which is that web-editor commit being caught exactly as intended.

A check that has only ever been seen to pass is not evidence that it works.

---

## Scope of contributions

This is a local preservation project. It does **not** send pull requests to
`BlackLabelHQ/RecompOne`:

- RecompOne's README states that AI-generated PRs will not be accepted and that
  AI-produced ports are unsupported.
- Any change RE15pc needs in the recompiler or runtime is therefore recorded as
  a patch in `patches/`, applied against a pinned upstream commit by
  `bootstrap.ps1`, and explained in `docs/compatibility.md`.

Keep changes to `RecompOne/` out of git entirely — that directory is ignored and
restored from upstream. Export the diff instead:

```sh
git -C RecompOne diff > patches/0001-<slug>.patch
git -C RecompOne checkout .        # leave the working clone clean
```

## Ground rules

- Never commit disc data. `Bio2Nov96.bin` is 124.3 MB and over GitHub's per-file
  limit, and it is copyrighted. `.gitignore` covers it; do not override that.
- Never commit anything under `out/`. Those are the port's diagnostic dumps - raw
  video memory, machine RAM and frame captures - and they contain decoded game art
  and overlay code. They are ignored for that reason, and it is the same reason the
  disc image is.
- Never commit anything under `generated/`. It is a transcription of the disc's
  executable.
- Never hand-edit anything under `generated/`. It is recompiler output. Change
  the config or the patches instead, and re-run the recompiler.
- Keep `.gitignore`, the hooks, and `bootstrap.ps1` honest. If a fresh clone
  cannot reproduce the build, that is a bug in this repository.
