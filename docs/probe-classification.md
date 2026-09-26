# Disc probe classification

Baseline produced by `recompone --probe-disc` against the disc recorded in
`disc-manifest.json`. This is the evidence the recompiler configuration in
`port/config/` is built on, so it is regenerated and diffed rather than trusted.

Regenerate:

```powershell
dotnet RecompOne/RecompOne.Recompiler/bin/Release/net10.0/recompone.dll ``
    --probe-disc Bio2Nov96.cue -json out/probe.json -all
pwsh -File tools/New-ProbeReport.ps1
```

## Summary

| Kind | Files |
|---|---|
| executable | 1 |
| rawcode | 7 |
| media | 69 |
| data | 257 |
| **total files** | **334** |

Boot executable resolves to `PSX.EXE`.

## Executable

| Path | LBA | Size | Base | Reason |
|---|---|---|---|---|
| `PSX.EXE` | 34260 | 718848 | `0x80010000` | PS-X EXE header |

## Code overlays

| Path | LBA | Size | Guessed base | Self-consistent? |
|---|---|---|---|---|
| `PSX/BIN/STAGE1.BIN` | 25 | 137648 | `0x80100000` | yes |
| `PSX/BIN/STAGE2.BIN` | 93 | 103216 | `0x80100000` | yes |
| `PSX/BIN/STAGE3.BIN` | 144 | 129036 | `0x80100000` | yes |
| `PSX/BIN/STAGE4.BIN` | 208 | 108064 | `0x80100000` | yes |
| `PSX/BIN/STAGE5.BIN` | 261 | 133280 | `0x80100000` | yes |
| `PSX/BIN/STAGE6.BIN` | 327 | 10324 | `0x8004F000` | **no - overlaps resident code** |
| `PSX/BIN/TITLE.BIN` | 333 | 9932 | *(none)* | **no - cannot be guessed** |

### The guessed bases are not trustworthy

Every base above is flagged `baseIsGuess`. Two of them are wrong, and the
failure modes are worth naming because they are not obvious from the output:

- **`STAGE6.BIN` is guessed as `0x8004F000`**, which lies inside the resident main
  executable (`0x80010000` + `0x0AF000` = `0x800BF000`). An overlay cannot load
  onto live code, so this base is impossible. `CodeScore.GuessBase` picked it
  because the overlay references main-executable data far more often (128 pointer
  hits in that page) than it references itself, and the heuristic takes the
  most-referenced page as the base.
- **`TITLE.BIN` gets no base at all.** It contains only 18 in-range pointers,
  below the function's `bestHits >= 32` confidence threshold, so it returns 0.
  `AutoConfigurator` treats a zero base as unanalysable and would silently drop
  the overlay, leaving the title screen unmapped.

The correct base for all seven is **`0x80100000`**, established independently:

1. The only free region below the stack (`0x801FFF00`) is `0x800BF000`-`0x801FFF00`.
2. Every overlay's self-referential pointers land in `0x8010xxxx`-`0x8011Bxxx`, and
   the largest overlay extent is `0x219B0`, so all seven fit in one slot.
3. `title` points at `0x80100000`, `0x80101000` and `0x80102000`, all within its own
   9932-byte image at that base - self-reference, not coincidence.
4. The extra pointers into `0x8004F000` and `0x800Bxxxx` are the overlays
   referencing main-executable globals, which is expected and does not imply
   a base.

So `port/config/bio2nov96.json` declares all seven bases explicitly instead of
accepting the guesses. That is the whole reason the configuration is hand-curated.

## Media

Classified by magic number or extension. No action needed beyond leaving them
out of the overlay list.

| Kind | Count |
|---|---|
| seq header | 32 |
| vb file | 23 |
| tim file | 13 |
| str file | 1 |

<details><summary>All media files</summary>

- `PSX/DATA/AAA.TIM` - 33312 bytes - tim file
- `PSX/DATA/C_BACK2.TIM` - 153620 bytes - tim file
- `PSX/DATA/CONFIG.TIM` - 33312 bytes - tim file
- `PSX/DATA/SELECT.TIM` - 66080 bytes - tim file
- `PSX/DATA/SELECTH.TIM` - 153620 bytes - tim file
- `PSX/DATA/SELECTH2.TIM` - 66080 bytes - tim file
- `PSX/DATA/SELECTH3.TIM` - 66080 bytes - tim file
- `PSX/DATA/ST_00.TIM` - 36640 bytes - tim file
- `PSX/DATA/TEX.TIM` - 165408 bytes - tim file
- `PSX/DATA/TITLEJ.TIM` - 153620 bytes - tim file
- `PSX/DATA/TITLEU.TIM` - 153620 bytes - tim file
- `PSX/DATA/YOUDIED.TIM` - 66080 bytes - tim file
- `PSX/EMD/SHITAI.TIM` - 16928 bytes - tim file
- `PSX/MOVIE/CAPCOM.STR` - 2304000 bytes - str file
- `PSX/SOUND/ARMS00.VB` - 35488 bytes - vb file
- `PSX/SOUND/ARMS01.VB` - 32432 bytes - vb file
- `PSX/SOUND/ARMS02.VB` - 34528 bytes - vb file
- `PSX/SOUND/ARMS03.VB` - 34528 bytes - vb file
- `PSX/SOUND/ARMS04.VB` - 34528 bytes - vb file
- `PSX/SOUND/ARMS05.VB` - 34528 bytes - vb file
- `PSX/SOUND/ARMS06.VB` - 34528 bytes - vb file
- `PSX/SOUND/ARMS07.VB` - 17392 bytes - vb file
- `PSX/SOUND/ARMS08.VB` - 35648 bytes - vb file
- `PSX/SOUND/ARMS09.VB` - 4400 bytes - vb file
- `PSX/SOUND/ARMS0A.VB` - 34528 bytes - vb file
- `PSX/SOUND/ARMS0B.VB` - 34528 bytes - vb file
- `PSX/SOUND/ARMS0C.VB` - 26896 bytes - vb file
- `PSX/SOUND/ARMS0D.VB` - 17392 bytes - vb file
- `PSX/SOUND/ARMS0E.VB` - 26896 bytes - vb file
- `PSX/SOUND/ARMS0F.VB` - 35648 bytes - vb file
- `PSX/SOUND/ARMS10.VB` - 35648 bytes - vb file
- `PSX/SOUND/ARMS11.VB` - 35648 bytes - vb file
- `PSX/SOUND/ARMS12.VB` - 35648 bytes - vb file
- `PSX/SOUND/ARMS13.VB` - 26896 bytes - vb file
- `PSX/SOUND/ARMS14.VB` - 31984 bytes - vb file
- `PSX/SOUND/CORE00.VB` - 32288 bytes - vb file
- `PSX/SOUND/CORE01.VB` - 34496 bytes - vb file
- `PSX/SOUND/MAIN00.BGM` - 128308 bytes - seq header
- `PSX/SOUND/MAIN01.BGM` - 152288 bytes - seq header
- `PSX/SOUND/MAIN02.BGM` - 221724 bytes - seq header
- `PSX/SOUND/MAIN03.BGM` - 69272 bytes - seq header
- `PSX/SOUND/MAIN04.BGM` - 104320 bytes - seq header
- `PSX/SOUND/MAIN05.BGM` - 215004 bytes - seq header
- `PSX/SOUND/MAIN06.BGM` - 210948 bytes - seq header
- `PSX/SOUND/MAIN07.BGM` - 103644 bytes - seq header
- `PSX/SOUND/MAIN09.BGM` - 198300 bytes - seq header
- `PSX/SOUND/MAIN0B.BGM` - 193036 bytes - seq header
- `PSX/SOUND/MAIN0D.BGM` - 227808 bytes - seq header
- `PSX/SOUND/MAIN0E.BGM` - 151184 bytes - seq header
- `PSX/SOUND/MAIN0F.BGM` - 190972 bytes - seq header
- `PSX/SOUND/MAIN10.BGM` - 49844 bytes - seq header
- `PSX/SOUND/MAIN12.BGM` - 188892 bytes - seq header
- `PSX/SOUND/MAIN13.BGM` - 218428 bytes - seq header
- `PSX/SOUND/MAIN14.BGM` - 217568 bytes - seq header
- `PSX/SOUND/MAIN15.BGM` - 211240 bytes - seq header
- `PSX/SOUND/MAIN16.BGM` - 242048 bytes - seq header
- `PSX/SOUND/MAIN3F.BGM` - 51396 bytes - seq header
- `PSX/SOUND/SUB_00.BGM` - 100804 bytes - seq header
- `PSX/SOUND/SUB_01.BGM` - 29176 bytes - seq header
- `PSX/SOUND/SUB_02.BGM` - 70484 bytes - seq header
- `PSX/SOUND/SUB_03.BGM` - 29548 bytes - seq header
- `PSX/SOUND/SUB_04.BGM` - 25148 bytes - seq header
- `PSX/SOUND/SUB_05.BGM` - 120920 bytes - seq header
- `PSX/SOUND/SUB_06.BGM` - 94820 bytes - seq header
- `PSX/SOUND/SUB_07.BGM` - 10512 bytes - seq header
- `PSX/SOUND/SUB_08.BGM` - 62256 bytes - seq header
- `PSX/SOUND/SUB_09.BGM` - 88552 bytes - seq header
- `PSX/SOUND/SUB_0A.BGM` - 168548 bytes - seq header
- `PSX/SOUND/SUB_3F.BGM` - 123744 bytes - seq header

</details>

## Data

None of these read as code. Importantly this includes every file the early plan
expected to be a false positive risk - `PSX/EMD/*.EMS`, `PSX/ITEM/*.ITP`,
`PSX/PLD/*.PLD` and `*.PLW`, `PSX/STAGE*/*.RDT` and `*.BSS`, `PSX/SOUND/*.BGM`,
`PSX/DOOR/*.DO2` - so the configuration needs no pruning beyond excluding them.

<details><summary>All data files</summary>

- `PSX/DATA/CORE00.ESP` - 7092 bytes
- `PSX/DATA/ITEMALL.PIX` - 86400 bytes
- `PSX/DATA/MAP01.PIX` - 32768 bytes
- `PSX/DATA/MAP02.PIX` - 32768 bytes
- `PSX/DATA/MAP03.PIX` - 32768 bytes
- `PSX/DATA/MAP04.PIX` - 32768 bytes
- `PSX/DATA/MAP05.PIX` - 32768 bytes
- `PSX/DATA/MAP06.PIX` - 32768 bytes
- `PSX/DATA/MAP07.PIX` - 32768 bytes
- `PSX/DATA/MAP08.PIX` - 32768 bytes
- `PSX/DATA/MAP09.PIX` - 32768 bytes
- `PSX/DATA/MAP0A.PIX` - 32768 bytes
- `PSX/DATA/MAP0B.PIX` - 32768 bytes
- `PSX/DATA/MAP0C.PIX` - 32768 bytes
- `PSX/DATA/MAP0D.PIX` - 32768 bytes
- `PSX/DATA/MIXITEM.PIX` - 16800 bytes
- `PSX/DATA/ROOM115U.SCD` - 396 bytes
- `PSX/DATA/ROOM506U.SCD` - 390 bytes
- `PSX/DOOR/DOOR00.DO2` - 96996 bytes
- `PSX/EMD/CDEMD0.EMS` - 4048896 bytes
- `PSX/EMD/CDEMD1.EMS` - 4055040 bytes
- `PSX/EMD/SHITAI.TM2` - 1396 bytes
- `PSX/ITEM/ITPS.ITP` - 872448 bytes
- `PSX/PLD/PL00.PLD` - 188940 bytes
- `PSX/PLD/PL00W00.PLW` - 20256 bytes
- `PSX/PLD/PL00W01.PLW` - 22960 bytes
- `PSX/PLD/PL00W02.PLW` - 21768 bytes
- `PSX/PLD/PL00W03.PLW` - 22856 bytes
- `PSX/PLD/PL00W04.PLW` - 22776 bytes
- `PSX/PLD/PL00W05.PLW` - 24368 bytes
- `PSX/PLD/PL00W06.PLW` - 21768 bytes
- `PSX/PLD/PL00W07.PLW` - 26888 bytes
- `PSX/PLD/PL00W08.PLW` - 29572 bytes
- `PSX/PLD/PL00W09.PLW` - 26792 bytes
- `PSX/PLD/PL00W0A.PLW` - 26792 bytes
- `PSX/PLD/PL00W0B.PLW` - 26792 bytes
- `PSX/PLD/PL00W0C.PLW` - 23104 bytes
- `PSX/PLD/PL00W0D.PLW` - 25612 bytes
- `PSX/PLD/PL00W0E.PLW` - 22776 bytes
- `PSX/PLD/PL00W0F.PLW` - 24940 bytes
- `PSX/PLD/PL00W10.PLW` - 23636 bytes
- `PSX/PLD/PL00W11.PLW` - 23636 bytes
- `PSX/PLD/PL00W12.PLW` - 24940 bytes
- `PSX/PLD/PL00W13.PLW` - 23544 bytes
- `PSX/PLD/PL00W14.PLW` - 23636 bytes
- `PSX/PLD/PL01.PLD` - 174140 bytes
- `PSX/PLD/PL02.PLD` - 175028 bytes
- `PSX/PLD/PL04.PLD` - 189888 bytes
- `PSX/PLD/PL04W00.PLW` - 18468 bytes
- `PSX/PLD/PL04W01.PLW` - 22756 bytes
- `PSX/PLD/PL04W02.PLW` - 19980 bytes
- `PSX/PLD/PL04W03.PLW` - 23912 bytes
- `PSX/PLD/PL04W04.PLW` - 22676 bytes
- `PSX/PLD/PL04W05.PLW` - 21768 bytes
- `PSX/PLD/PL04W06.PLW` - 22852 bytes
- `PSX/PLD/PL04W07.PLW` - 26708 bytes
- `PSX/PLD/PL04W08.PLW` - 29392 bytes
- `PSX/PLD/PL04W09.PLW` - 23636 bytes
- `PSX/PLD/PL04W0A.PLW` - 21768 bytes
- `PSX/PLD/PL04W0B.PLW` - 20256 bytes
- `PSX/PLD/PL04W0C.PLW` - 22884 bytes
- `PSX/PLD/PL04W0D.PLW` - 25432 bytes
- `PSX/PLD/PL04W0E.PLW` - 21768 bytes
- `PSX/PLD/PL04W0F.PLW` - 22776 bytes
- `PSX/PLD/PL04W10.PLW` - 24940 bytes
- `PSX/PLD/PL04W11.PLW` - 23636 bytes
- `PSX/PLD/PL04W12.PLW` - 20256 bytes
- `PSX/PLD/PL04W13.PLW` - 23332 bytes
- `PSX/PLD/PL04W14.PLW` - 20256 bytes
- `PSX/PLD/PL05.PLD` - 174136 bytes
- `PSX/PLD/PL06.PLD` - 174552 bytes
- `PSX/PLD/PL0D.PLD` - 159912 bytes
- `PSX/PLD/PL0E.PLD` - 123456 bytes
- `PSX/PLD/PL0F.PLD` - 128404 bytes
- `PSX/PLD/PL0FW00.PLW` - 15648 bytes
- `PSX/SOUND/ARMS00.EDH` - 3128 bytes
- `PSX/SOUND/ARMS01.EDH` - 3152 bytes
- `PSX/SOUND/ARMS02.EDH` - 3144 bytes
- `PSX/SOUND/ARMS03.EDH` - 3144 bytes
- `PSX/SOUND/ARMS04.EDH` - 3144 bytes
- `PSX/SOUND/ARMS05.EDH` - 3144 bytes
- `PSX/SOUND/ARMS06.EDH` - 3144 bytes
- `PSX/SOUND/ARMS07.EDH` - 3144 bytes
- `PSX/SOUND/ARMS08.EDH` - 3152 bytes
- `PSX/SOUND/ARMS09.EDH` - 3156 bytes
- `PSX/SOUND/ARMS0A.EDH` - 3144 bytes
- `PSX/SOUND/ARMS0B.EDH` - 3144 bytes
- `PSX/SOUND/ARMS0C.EDH` - 3144 bytes
- `PSX/SOUND/ARMS0D.EDH` - 3152 bytes
- `PSX/SOUND/ARMS0E.EDH` - 3144 bytes
- `PSX/SOUND/ARMS0F.EDH` - 3152 bytes
- `PSX/SOUND/ARMS10.EDH` - 3152 bytes
- `PSX/SOUND/ARMS11.EDH` - 3152 bytes
- `PSX/SOUND/ARMS12.EDH` - 3152 bytes
- `PSX/SOUND/ARMS13.EDH` - 3144 bytes
- `PSX/SOUND/ARMS14.EDH` - 3164 bytes
- `PSX/SOUND/CORE00.EDH` - 3176 bytes
- `PSX/SOUND/CORE01.EDH` - 3176 bytes
- `PSX/STAGE1/ROOM102.BSS` - 851968 bytes
- `PSX/STAGE1/ROOM1020.RDT` - 158400 bytes
- `PSX/STAGE1/ROOM1021.RDT` - 159052 bytes
- `PSX/STAGE1/ROOM103.BSS` - 851968 bytes
- `PSX/STAGE1/ROOM1030.RDT` - 197692 bytes
- `PSX/STAGE1/ROOM1031.RDT` - 189708 bytes
- `PSX/STAGE1/ROOM104.BSS` - 458752 bytes
- `PSX/STAGE1/ROOM1040.RDT` - 213288 bytes
- `PSX/STAGE1/ROOM1041.RDT` - 213288 bytes
- `PSX/STAGE1/ROOM1060.RDT` - 1404 bytes
- `PSX/STAGE1/ROOM107.BSS` - 524288 bytes
- `PSX/STAGE1/ROOM1070.RDT` - 181076 bytes
- `PSX/STAGE1/ROOM1071.RDT` - 142200 bytes
- `PSX/STAGE1/ROOM109.BSS` - 1048576 bytes
- `PSX/STAGE1/ROOM1090.RDT` - 216124 bytes
- `PSX/STAGE1/ROOM1091.RDT` - 215240 bytes
- `PSX/STAGE1/ROOM10B.BSS` - 589824 bytes
- `PSX/STAGE1/ROOM10B0.RDT` - 77724 bytes
- `PSX/STAGE1/ROOM10B1.RDT` - 78352 bytes
- `PSX/STAGE1/ROOM110.BSS` - 851968 bytes
- `PSX/STAGE1/ROOM1100.RDT` - 127392 bytes
- `PSX/STAGE1/ROOM1101.RDT` - 127392 bytes
- `PSX/STAGE1/ROOM115.BSS` - 524288 bytes
- `PSX/STAGE1/ROOM1150.RDT` - 140216 bytes
- `PSX/STAGE1/ROOM1151.RDT` - 179092 bytes
- `PSX/STAGE1/ROOM116.BSS` - 262144 bytes
- `PSX/STAGE1/ROOM1160.RDT` - 36460 bytes
- `PSX/STAGE1/ROOM1161.RDT` - 36460 bytes
- `PSX/STAGE1/ROOM117.BSS` - 393216 bytes
- `PSX/STAGE1/ROOM1170.RDT` - 153976 bytes
- `PSX/STAGE1/ROOM1171.RDT` - 154964 bytes
- `PSX/STAGE1/ROOM118.BSS` - 655360 bytes
- `PSX/STAGE1/ROOM1180.RDT` - 86640 bytes
- `PSX/STAGE1/ROOM119.BSS` - 786432 bytes
- `PSX/STAGE1/ROOM1190.RDT` - 179584 bytes
- `PSX/STAGE1/ROOM1191.RDT` - 179584 bytes
- `PSX/STAGE1/ROOM11A.BSS` - 196608 bytes
- `PSX/STAGE1/ROOM11A0.RDT` - 85076 bytes
- `PSX/STAGE1/ROOM11A1.RDT` - 85076 bytes
- `PSX/STAGE1/ROOM11B.BSS` - 851968 bytes
- `PSX/STAGE1/ROOM11B0.RDT` - 174256 bytes
- `PSX/STAGE1/ROOM11B1.RDT` - 173436 bytes
- `PSX/STAGE1/ROOM11C.BSS` - 917504 bytes
- `PSX/STAGE1/ROOM11C0.RDT` - 105672 bytes
- `PSX/STAGE1/ROOM11C1.RDT` - 104568 bytes
- `PSX/STAGE1/ROOM11E.BSS` - 983040 bytes
- `PSX/STAGE1/ROOM11E0.RDT` - 166388 bytes
- `PSX/STAGE1/ROOM11E1.RDT` - 166388 bytes
- `PSX/STAGE1/ROOM11F.BSS` - 458752 bytes
- `PSX/STAGE1/ROOM120.BSS` - 262144 bytes
- `PSX/STAGE1/ROOM1200.RDT` - 167096 bytes
- `PSX/STAGE1/ROOM1201.RDT` - 167104 bytes
- `PSX/STAGE1/ROOM121.BSS` - 589824 bytes
- `PSX/STAGE1/ROOM1210.RDT` - 140484 bytes
- `PSX/STAGE1/ROOM1211.RDT` - 140468 bytes
- `PSX/STAGE1/ROOM122.BSS` - 131072 bytes
- `PSX/STAGE1/ROOM123.BSS` - 131072 bytes
- `PSX/STAGE1/ROOM124.BSS` - 131072 bytes
- `PSX/STAGE1/ROOM125.BSS` - 131072 bytes
- `PSX/STAGE1/ROOM126.BSS` - 131072 bytes
- `PSX/STAGE2/ROOM200.BSS` - 1048576 bytes
- `PSX/STAGE2/ROOM2000.RDT` - 246496 bytes
- `PSX/STAGE2/ROOM2001.RDT` - 246496 bytes
- `PSX/STAGE2/ROOM201.BSS` - 851968 bytes
- `PSX/STAGE2/ROOM203.BSS` - 524288 bytes
- `PSX/STAGE2/ROOM2030.RDT` - 233480 bytes
- `PSX/STAGE2/ROOM207.BSS` - 917504 bytes
- `PSX/STAGE2/ROOM2070.RDT` - 267092 bytes
- `PSX/STAGE2/ROOM208.BSS` - 458752 bytes
- `PSX/STAGE2/ROOM209.BSS` - 720896 bytes
- `PSX/STAGE2/ROOM2090.RDT` - 102044 bytes
- `PSX/STAGE2/ROOM20B.BSS` - 1048576 bytes
- `PSX/STAGE2/ROOM20B0.RDT` - 144668 bytes
- `PSX/STAGE2/ROOM20B1.RDT` - 110316 bytes
- `PSX/STAGE3/ROOM300.BSS` - 262144 bytes
- `PSX/STAGE3/ROOM3000.RDT` - 114644 bytes
- `PSX/STAGE3/ROOM3001.RDT` - 114640 bytes
- `PSX/STAGE3/ROOM301.BSS` - 655360 bytes
- `PSX/STAGE3/ROOM3010.RDT` - 166784 bytes
- `PSX/STAGE3/ROOM3011.RDT` - 166784 bytes
- `PSX/STAGE3/ROOM302.BSS` - 983040 bytes
- `PSX/STAGE3/ROOM3020.RDT` - 117496 bytes
- `PSX/STAGE3/ROOM303.BSS` - 131072 bytes
- `PSX/STAGE3/ROOM3030.RDT` - 80940 bytes
- `PSX/STAGE3/ROOM3031.RDT` - 80940 bytes
- `PSX/STAGE3/ROOM304.BSS` - 983040 bytes
- `PSX/STAGE3/ROOM3040.RDT` - 72412 bytes
- `PSX/STAGE3/ROOM3041.RDT` - 72412 bytes
- `PSX/STAGE3/ROOM306.BSS` - 458752 bytes
- `PSX/STAGE3/ROOM3060.RDT` - 129380 bytes
- `PSX/STAGE3/ROOM3061.RDT` - 130140 bytes
- `PSX/STAGE3/ROOM307.BSS` - 851968 bytes
- `PSX/STAGE3/ROOM3070.RDT` - 188904 bytes
- `PSX/STAGE3/ROOM3071.RDT` - 189572 bytes
- `PSX/STAGE3/ROOM308.BSS` - 262144 bytes
- `PSX/STAGE3/ROOM3080.RDT` - 169428 bytes
- `PSX/STAGE3/ROOM3081.RDT` - 97376 bytes
- `PSX/STAGE3/ROOM309.BSS` - 1048576 bytes
- `PSX/STAGE3/ROOM3090.RDT` - 184816 bytes
- `PSX/STAGE3/ROOM3091.RDT` - 220184 bytes
- `PSX/STAGE3/ROOM30E.BSS` - 131072 bytes
- `PSX/STAGE3/ROOM30E0.RDT` - 14772 bytes
- `PSX/STAGE3/ROOM30E1.RDT` - 14796 bytes
- `PSX/STAGE4/ROOM400.BSS` - 524288 bytes
- `PSX/STAGE4/ROOM4000.RDT` - 212248 bytes
- `PSX/STAGE4/ROOM4001.RDT` - 176176 bytes
- `PSX/STAGE4/ROOM401.BSS` - 262144 bytes
- `PSX/STAGE4/ROOM4010.RDT` - 245312 bytes
- `PSX/STAGE4/ROOM4011.RDT` - 244204 bytes
- `PSX/STAGE4/ROOM403.BSS` - 393216 bytes
- `PSX/STAGE4/ROOM4030.RDT` - 167652 bytes
- `PSX/STAGE4/ROOM4031.RDT` - 167692 bytes
- `PSX/STAGE4/ROOM404.BSS` - 851968 bytes
- `PSX/STAGE4/ROOM408.BSS` - 851968 bytes
- `PSX/STAGE4/ROOM409.BSS` - 262144 bytes
- `PSX/STAGE4/ROOM4090.RDT` - 20144 bytes
- `PSX/STAGE4/ROOM4091.RDT` - 20144 bytes
- `PSX/STAGE4/ROOM40A.BSS` - 524288 bytes
- `PSX/STAGE4/ROOM40A0.RDT` - 24500 bytes
- `PSX/STAGE4/ROOM40A1.RDT` - 24604 bytes
- `PSX/STAGE5/ROOM501.BSS` - 262144 bytes
- `PSX/STAGE5/ROOM5010.RDT` - 223800 bytes
- `PSX/STAGE5/ROOM5011.RDT` - 224588 bytes
- `PSX/STAGE5/ROOM503.BSS` - 720896 bytes
- `PSX/STAGE5/ROOM5030.RDT` - 166836 bytes
- `PSX/STAGE5/ROOM5031.RDT` - 166836 bytes
- `PSX/STAGE5/ROOM504.BSS` - 458752 bytes
- `PSX/STAGE5/ROOM5040.RDT` - 209068 bytes
- `PSX/STAGE5/ROOM5041.RDT` - 209068 bytes
- `PSX/STAGE5/ROOM506.BSS` - 851968 bytes
- `PSX/STAGE5/ROOM5060.RDT` - 211916 bytes
- `PSX/STAGE5/ROOM5061.RDT` - 211916 bytes
- `PSX/STAGE5/ROOM50A.BSS` - 917504 bytes
- `PSX/STAGE5/ROOM50A0.RDT` - 156972 bytes
- `PSX/STAGE5/ROOM50A1.RDT` - 136608 bytes
- `PSX/STAGE5/ROOM50C.BSS` - 458752 bytes
- `PSX/STAGE5/ROOM50C0.RDT` - 105664 bytes
- `PSX/STAGE5/ROOM50C1.RDT` - 105648 bytes
- `PSX/STAGE5/ROOM50D.BSS` - 65536 bytes
- `PSX/STAGE5/ROOM510.BSS` - 655360 bytes
- `PSX/STAGE5/ROOM5100.RDT` - 117348 bytes
- `PSX/STAGE5/ROOM5101.RDT` - 117348 bytes
- `PSX/STAGE5/ROOM511.BSS` - 458752 bytes
- `PSX/STAGE5/ROOM5110.RDT` - 67996 bytes
- `PSX/STAGE5/ROOM5111.RDT` - 67996 bytes
- `PSX/STAGE5/ROOM512.BSS` - 458752 bytes
- `PSX/STAGE5/ROOM5120.RDT` - 165336 bytes
- `PSX/STAGE5/ROOM5121.RDT` - 165336 bytes
- `PSX/STAGE5/ROOM514.BSS` - 917504 bytes
- `PSX/STAGE5/ROOM5140.RDT` - 141924 bytes
- `PSX/STAGE5/ROOM5141.RDT` - 131620 bytes
- `PSX/STAGE6/ROOM603.BSS` - 393216 bytes
- `PSX/STAGE6/ROOM6030.RDT` - 132372 bytes
- `PSX/STAGE6/ROOM6031.RDT` - 131688 bytes
- `PSX/STAGE6/ROOM604.BSS` - 458752 bytes
- `PSX/STAGE6/ROOM6040.RDT` - 132148 bytes
- `PSX/STAGE6/ROOM6041.RDT` - 132260 bytes
- `SYSTEM.CNF` - 65 bytes
- `ZNULL.DAT` - 37044000 bytes

</details>

---

## Upstream formatting note

The `reason` strings report code-density scores through a culture-sensitive
`{0:0.00}` format, so on a machine using a comma decimal separator they read
`(0,92, 388 returns, ...)` rather than `(0.92, 388 returns, ...)`. Cosmetic only;
the underlying values are unaffected.
