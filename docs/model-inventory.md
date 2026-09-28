# Model-file structure inventory

`tools/New-ModelInventory.ps1` reads the identified disc and records relative
section directories without exporting model payloads.

```powershell
pwsh -File tools/New-ModelInventory.ps1
pwsh -File tools/New-ModelInventory.ps1 -Check
```

The current inventory structurally accounts for all 54 EMS, PLD and PLW files:

- CDEMD0.EMS: 25 sector-aligned blocks.
- CDEMD1.EMS: 25 sector-aligned blocks.
- Nine PLD files: one block each.
- Forty-three PLW files: one block each.
- Total: 102 blocks.

For each block, the directory offset, section count, section offsets and end
must fit the file. Loose PLD/PLW files must end at their directory end; EMS
members advance to the next 2048-byte boundary. Unrecognized or trailing data
is reported as unresolved rather than silently skipped.

`-Check` reconstructs the metadata from the disc and compares it with the
saved index. A negative control changing a recorded extent is rejected.

This proves structural accounting only. It does not assign enemy identities,
enumerate every animation, prove model decoding, or demonstrate playability.
Those remain separate coverage requirements. The index contains metadata only;
no game model, texture, animation or recompiled code is distributed.
