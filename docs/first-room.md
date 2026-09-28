# First playable room acceptance

## Reproduce

```powershell
dotnet build tests/RE15pc.Checks/RE15pc.Checks.csproj -c Release
pwsh -File tools/Test-FirstRoom.ps1
```

The gate cold-boots the actual disc and stops at exactly 2700 guest frames.
It requires the normal strict runtime, audio, artifact and 49-pair overlay
checks to pass before testing the gameplay observations.

Latest evidence: `out/gates/first-room-v1/first-room.json`, PASS.
Raw images, RAM and logs remain local under that directory.

## What the gate proves

- Leon begins in rooftop room `117`, camera 0.
- Right changes facing while XYZ remain constant.
- Up then changes world position deliberately.
- At the railing, forward Z travel stops while X continues along the wall
  and Y remains constant. This is a collision/wall-sliding check, not a frozen
  frame being interpreted as collision.
- The delivered BIOS pad trace contains the requested Right, Up and Square.
- Square is issued inside the door rectangle defined in this disc's
  `ROOM1170.RDT` init script: X 1500..3599, Z 14400..16099.
- The destination is room `116`, camera 3, floor 4. Player XYZ
  `(-11710, -7200, -26500)` and facing `2957` match the original door record.
- The final resident RDT header matches `PSX/STAGE1/ROOM1160.RDT` after
  normalizing relocated pointers. A camera change alone cannot pass this check.

The route is:

```text
90:start,240:cross,300:cross,600:up:600,1250:cross,
1800:right:17,1840:up:40,1890:square
```

The prototype uses Cross for menu confirmation and Square for gameplay action.
The default PC mappings are Z for Cross and A for Square. Pressing Cross at the
rooftop gate does not activate it; the negative approach run remains in room 117.

## Read-only state probes

The host samples the original guest state without changing it:

| Address | Meaning and evidence |
|---|---|
| 0x800ACA88 / 8C / 90 | Player XYZ, referenced by the interaction handler at 0x8002D474 |
| 0x800ACABE | Player facing, updated by directional input handlers around 0x80032974 |
| 0x800B0FE4 | Camera index used to index room camera records |
| 0x800B0FE2 | Room index; 0x17 becomes 0x16 on this route |
| 0x800AC778 | Resident RDT pointer; allocation is reused across the transition |

The player structure starts at 0x800ACA54. The original gameplay button table
at 0x80073DBC maps the interaction flags to Square, accounting for BIOS byte
order. These addresses are specific to the hashed November 1996 build.

## Boundaries

This completes the first-room gate, not full-game acceptance. Other rooms,
scenarios, scripted branches, interactions, enemies, items, weapons, sound,
movies, persistence and long-session stability still require their own evidence.
