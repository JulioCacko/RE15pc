# Overlay load: RAM is the file, verbatim

Settles the Phase 5 open question in `docs/phases.md`: the loader copies each
overlay `.BIN` into RAM **verbatim, including the leading 4-byte word**. `skip`
stays `0` in `port/config/bio2nov96.json`.

## The question

Every one of the seven overlays begins with a small dword followed by code or
data: STAGE1 `0x0000000F`, TITLE `0x0000000E`, STAGE6 `0x00000014`. All seven
load at base `0x80100000`. If the loader stripped that word, then RAM would
equal the file shifted by 4 bytes and every address in the overlay function maps
would be wrong by 4 - which would mean each overlay needs `"skip": 4`.

Two mutually exclusive hypotheses, both decided by one byte comparison:

- **A - verbatim:** `ram[i] == file[i]` for all `i`.
- **B - header stripped:** `ram[i] == file[i + 4]`.

## Method

### 1. Extract the overlay file from the disc image

`Bio2Nov96.bin` is a raw MODE2/2352 image, so the file cannot be opened directly.
User data begins 24 bytes into each 2352-byte sector. The reader is the one in
`tools/New-DiscManifest.ps1:103-134` (`Read-UserSectors` / `Read-Extent`, seek to
`lba * 2352 + 24`, read 2048).

The extent was taken from `disc-manifest.json`:

| File | LBA | Size | Base |
|---|---|---|---|
| `PSX/BIN/TITLE.BIN` | 333 | 9932 | `0x80100000` |
| `PSX/BIN/STAGE1.BIN` | 25 | 137648 | `0x80100000` |

Those LBAs were independently re-derived in this investigation by walking the
ISO9660 tree in the image itself (PVD at LBA 16, magic `CD001`, root directory
record at LBA 22), which returned the same extents - `PSX/BIN/TITLE.BIN` LBA 333
size 9932, `PSX/BIN/STAGE1.BIN` LBA 25 size 137648, `PSX.EXE` LBA 34260 size
718848, `SYSTEM.CNF` LBA 34259 size 65. The extraction is therefore reading the
extent the manifest names, not an offset guess.

### 2. Establish what the RAM dump is

`out/diagnostics/ram-overlay-80100000.bin` is 139264 bytes = `0x22000`, produced by
`RunReport.WriteArtifacts` (`port/RE15pc/Diagnostics/RunReport.cs:156`):

```csharp
var overlay = SliceRam(mem, OverlayBase, OverlayWindow);
File.WriteAllBytes(Path.Combine(outDir, "ram-overlay-80100000.bin"), overlay);
```

with `OverlayBase = 0x8010_0000` (`RunReport.cs:61`) and
`OverlayWindow = 0x22000` (`RunReport.cs:67`). `SliceRam` (`RunReport.cs:191-204`)
masks the guest address with `Runtime.RamSize - 1`, and retail RAM is
`0x00200000` (`RecompOne.Runtime/Memory/MemoryMap.cs:7`), so dump offset 0 is
guest address `0x80100000` exactly.

Provenance check: the SHA256 of the dump in hand is

```
F607367721EC9BE7F1D1143F2A4BAF57F158174CA98F7A32E9BCC25CF7199B23
```

which is byte-identical to `sha256 (RAM now)` in `out/diagnostics/report.txt:16`.
The dump belongs to the recorded run - the one whose console shows
`[Dispatcher] loaded overlay: main` followed by `[Dispatcher] loaded overlay: title`
and then 30 seconds of silence.

**Caveat on that run.** `report.txt:22-26` prints boilerplate telling the reader to
compare the window against `STAGE1.BIN`. In this particular run `stage1` was never
resident - the log shows `main` then `title`, and `overlays loaded : 2`. The
resident overlay is `title`, so TITLE.BIN is the positive test and STAGE1.BIN is
the negative control.

### 3. Compare

For each hypothesis, count the matching prefix length from offset 0, and also
count every differing byte across the compared range rather than only the first.

## Raw evidence

### First 32 bytes, TITLE.BIN vs RAM at `0x80100000`

```
file (LBA 333, 9932 bytes)
  0E 00 00 00 53 65 6C 65 63 74 68 33 2E 74 69 6D 00 00 00 00 53 65 6C 65 63 74 68 2E 74 69 6D 00

RAM offset 0
  0E 00 00 00 53 65 6C 65 63 74 68 33 2E 74 69 6D 00 00 00 00 53 65 6C 65 63 74 68 2E 74 69 6D 00
```

ASCII on both sides: `\x0E\x00\x00\x00` then `Selecth3.tim` then `Selecth.tim`.
The leading word is present in RAM, at RAM offset 0. Hypothesis B is already dead
here: it requires `ram[0] == 0x53` (`'S'`).

### Match counts, all seven overlays

| Overlay | Size | A: `ram[i]==file[i]` prefix | B: `ram[i]==file[i+4]` prefix |
|---|---|---|---|
| `title` | 9932 | **9924** | **0** |
| `stage1` | 137648 | 0 | 0 |
| `stage2` | 103216 | 0 | 0 |
| `stage3` | 129036 | 0 | 0 |
| `stage4` | 108064 | 0 | 0 |
| `stage5` | 133280 | 0 | 0 |
| `stage6` | 10324 | 0 | 0 |

The `title` match is specific, not accidental: none of the other six overlays
matches even one byte at RAM offset 0, under either rule. STAGE1 is the clean
negative control - its first byte is `0x0F`, RAM holds `0x0E`.

```
stage1 first 32
  0F 00 00 00 9C FF FF FF A8 FD FF FF 00 00 00 00 00 00 00 00 00 00 00 00 64 00 00 00 00 00 00 00
RAM first 32
  0E 00 00 00 53 65 6C 65 63 74 68 33 2E 74 69 6D 00 00 00 00 53 65 6C 65 63 74 68 2E 74 69 6D 00
```

### TITLE.BIN vs RAM: the whole extent

Compared over the full 9932-byte file extent, **exactly one byte differs**:

| Offset | Guest address | File | RAM |
|---|---|---|---|
| 9924 (`0x26C4`) | `0x801026C4` | `0x00` | `0x02` |

9931 of 9932 bytes are equal. Interior windows agree byte for byte:

```
offset 0x0800  file 25 20 82 00 08 00 E0 03 00 00 A4 AC 10 80 04 3C
               ram  25 20 82 00 08 00 E0 03 00 00 A4 AC 10 80 04 3C
offset 0x1000  file 25 18 62 00 DA FF A0 14 00 00 83 AC FF 00 06 3C
               ram  25 18 62 00 DA FF A0 14 00 00 83 AC FF 00 06 3C
offset 0x2000  file 0B 80 04 3C 38 CA 84 24 10 80 05 3C C4 26 A5 24
               ram  0B 80 04 3C 38 CA 84 24 10 80 05 3C C4 26 A5 24
offset 0x26B0  file AC 21 10 80 D0 23 10 80 94 25 10 80 F0 25 10 80
               ram  AC 21 10 80 D0 23 10 80 94 25 10 80 F0 25 10 80
```

Hashes make the result exact. The first 9924 bytes are the same bytes:

```
sha256 TITLE.BIN[0..9923] : 12AB33AFE6D038D675F1EC5EE755767085F100FD0384E20AC73B4A697CAD3276
sha256 RAM      [0..9923] : 12AB33AFE6D038D675F1EC5EE755767085F100FD0384E20AC73B4A697CAD3276
```

Zeroing that single byte in each 9932-byte extent makes the two extents identical,
and identical to the file itself:

```
sha256 TITLE.BIN (9932 bytes)                       : 373FDD7428227D6FBF902135FF3087481AE0F19D93BB75F16A01F5760270C563
sha256 TITLE[0..9931] with byte@9924 forced to 0x00 : 373FDD7428227D6FBF902135FF3087481AE0F19D93BB75F16A01F5760270C563
sha256 RAM  [0..9931] with byte@9924 forced to 0x00 : 373FDD7428227D6FBF902135FF3087481AE0F19D93BB75F16A01F5760270C563
```

### The one differing byte is a guest write, not a loader artifact

Offset 9924 lies inside the file's trailing zero region, in the overlay's own data
area, and the overlay's own emitted code writes it. `generated/title.cs:2373-2374`,
inside `func_80102038` (declared at `title.cs:2320`):

```csharp
c.At = 0x80100000u; ...                                        /* 0x801020E8  lui  $at, 0x8010 */
mem.WriteU8((c.At + 0x26C4u), (byte)c.V0); ...                 /* 0x801020EC  sb   $v0, 0x26C4($at) */
```

and `generated/title.cs:2262-2263` clears the same byte:

```csharp
c.At = 0x80100000u; ...                                        /* 0x80101F90  lui  $at, 0x8010 */
mem.WriteU8((c.At + 0x26C4u), (byte)0u); ...                   /* 0x80101F94  sb   $zero, 0x26C4($at) */
```

`0x801026C4` is a one-byte overlay global (a menu index - the same routine reads it
at `title.cs:2267`, scales it by 4 and indexes a pointer table at `0x8010269C`).
The file holds its initial value `0x00`; the running title screen had left `0x02`
there when the dump was taken. That is a post-load write by guest code.

### Corroboration: the overlay's own absolute addressing fixes shift 0

This is independent of the RAM diff. The pointer table the title code dispatches
through is addressed absolutely as `0x80100000 + 0x269C` (`title.cs:2260`, `:2268`,
`:2270`), and the file's bytes at offset `0x269C` are:

| File offset | Guest address | Dword | Emitted function |
|---|---|---|---|
| 0x269C | 0x8010269C | `0x80101FE0` | `func_80101FE0` |
| 0x26A0 | 0x801026A0 | `0x80102038` | `func_80102038` |
| 0x26A4 | 0x801026A4 | `0x80102100` | `func_80102100` |
| 0x26A8 | 0x801026A8 | `0x80102140` | `func_80102140` |
| 0x26AC | 0x801026AC | `0x80102174` | `func_80102174` |
| 0x26B0 | 0x801026B0 | `0x801021AC` | `func_801021AC_title` (`title.cs:2435`) |
| 0x26B4 | 0x801026B4 | `0x801023D0` | `func_801023D0` |
| 0x26B8 | 0x801026B8 | `0x80102594` | `func_80102594` |
| 0x26BC | 0x801026BC | `0x801025F0` | `func_801025F0` (`title.cs:2774`) |

Nine in-range pointers, every one of them a real emitted entry point. The guest
reaches that table at absolute address `0x8010269C`, so the table must live at
**file offset `0x269C`**. Under hypothesis B it would have to sit at file offset
`0x26A0` and every entry would be off by one slot. The guest's own addressing and
the file layout only line up at shift 0.

## Conclusion

**RAM equals the file content from offset 0. Verbatim - no header is stripped.**

- TITLE.BIN vs RAM at `0x80100000`: **9924 bytes match from offset 0**; the first
  and only mismatch is at offset 9924 (`file 0x00` vs `ram 0x02`), 9931 of 9932
  bytes equal overall. Under the shift-by-4 rule the match would be **0 bytes**.
- STAGE1.BIN vs the same RAM: **0 bytes match** under either rule, as expected -
  stage1 was not resident. This is what makes the title result specific.
- RAM offset 0 is `0E 00 00 00 53 65 6C 65 63 74 68 33 2E 74 69 6D`, i.e. the
  file's own leading `0x0000000E` word followed by `Selecth3.tim`. That word is
  part of the RAM image.

Nothing in the port or the runtime strips a header either. Overlay dispatch only
registers function maps and does not copy bytes
(`RecompOne/RecompOne.Runtime/Dispatch/Dispatcher.cs:81-104`); the host copies only
`PSX.EXE`, at `0x80010000`, with no skip (`generated/Entry.cs:27`); and the CD HLE
copy path writes `data[offset + i]` with `offset` defaulting to 0
(`RecompOne/RecompOne.Runtime/Cdrom/CdController.cs:183-192`). Overlay dispatch is
triggered from that same CD read path (`RecompOne/RecompOne.Runtime/sdk/LibCd.cs:160`,
`LibDs.cs:729`).

## Consequence for `port/config/bio2nov96.json`

**`skip` must stay 0.** Every overlay entry in the config omits the key, and the
recompiler's default is already 0 - `ConfigLoader.cs:161-162`:

```csharp
[JsonPropertyName("offset")] public int Offset { get; set; } = 0;
[JsonPropertyName("skip")]   public int Skip   { get; set; } = 0;
```

So no edit is needed: leaving `skip` out is the correct and verified setting. The
overlay function maps, swept at base `0x80100000`, are addressed correctly as
emitted.

`LbaStart` is unaffected by this decision either way. `OverlayWriter.cs:438-442`
derives it as

```csharp
var absLba = lba + (cfg.Offset + cfg.Skip) / 2048;
var full = fs.ReadFile(cfg.File);
var start = cfg.Offset + cfg.Skip;
```

with integer division, and `(0 + 4) / 2048 == 0`, so even a hypothetical `skip: 4`
would have left the LBA at 333. The generated tables confirm the current values:
`generated/title.cs:2796-2798` declares `LbaStart => 333` and `Size => 0x26CCu`
(9932) - the whole extent, header word included.

The config's own comment at `port/config/bio2nov96.json:82-85`, which records this
as still open and says `"Until then "skip" is left at 0"`, is now resolved in
favour of the value it already had. The comment can be rewritten to state the
finding; no functional key changes.

## Reproducing

Read-only; needs only the disc image, the RAM dump, and PowerShell.

```powershell
$stream = [System.IO.File]::Open((Resolve-Path 'Bio2Nov96.bin'), 'Open', 'Read', 'Read')
function Read-Extent([int]$Lba, [int]$Size) {          # MODE2/2352: user data at +24
    $sectors = [int][Math]::Ceiling($Size / 2048.0)
    $dest = New-Object byte[] ($sectors * 2048); $sector = New-Object byte[] 2048
    for ($i = 0; $i -lt $sectors; $i++) {
        $stream.Seek(([long]($Lba + $i) * 2352) + 24, 'Begin') | Out-Null
        $read = 0
        while ($read -lt 2048) { $n = $stream.Read($sector, $read, 2048 - $read); if ($n -le 0) { break }; $read += $n }
        [Array]::Copy($sector, 0, $dest, $i * 2048, 2048)
    }
    $o = New-Object byte[] $Size; [Array]::Copy($dest, 0, $o, 0, $Size); return , $o
}
$title = Read-Extent -Lba 333 -Size 9932
$ram   = [System.IO.File]::ReadAllBytes((Resolve-Path 'out/diagnostics/ram-overlay-80100000.bin'))

$a = 0; while ($a -lt 9932 -and $title[$a] -eq $ram[$a]) { $a++ }        # verbatim prefix
$b = 0; while (($b + 4) -lt 9932 -and $title[$b + 4] -eq $ram[$b]) { $b++ }  # shifted prefix
"verbatim prefix = $a ; shifted prefix = $b"     # -> verbatim prefix = 9924 ; shifted prefix = 0
$stream.Dispose()
```

Re-run `RE15pc` with `stage1` resident and this same comparison should hold for
STAGE1.BIN against the same window; the method above is unchanged, only the LBA
(25), size (137648) and which overlay is live differ.

## What this does not settle

- The overlay window is only `0x22000` bytes, so the comparison covers the largest
  overlay (STAGE1, `0x219B0`) but nothing beyond it. All seven are inside the
  window, so all seven can be compared.
- This does not explain the crash at the end of the run (process exit
  `0xC0000409`) or the 30 seconds of silence after the `title` load. The dump is
  the state as of the smoke window elapsing, and the overlay load is demonstrably
  correct; the fault lies elsewhere.
- It does not establish which code path performed the RAM copy in this run. It
  establishes that whatever performed it copied the file verbatim from offset 0.
