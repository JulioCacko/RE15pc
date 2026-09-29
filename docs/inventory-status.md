# Inventory acceptance status

## Verified open and close

Start opens the inventory; Cross closes it. Triangle did not open it in the
measured starting-room case.

```powershell
pwsh -File tools/Test-InventoryMenu.ps1 -OpenRun out/runs/elza-inventory-start -ClosedRun out/runs/elza-item-confirm
```

This gate passes against actual native runs. It checks the menu-open flag,
all ten four-byte inventory slots, and unchanged player XYZ. The rendered open
menu was visually inspected and its item quantities agree with RAM.

The shared route prefix is:
`90:start,240:cross,300:cross,600:right,1250:cross,1800:start`.
The close run adds `2100:cross`.

Read-only state observations now include:
- menu mode 1–5 at 0x800B5359 (zero is gameplay);
- control-blocked bit 0x40 at 0x800ACA3C, which is also used while aiming and is not sufficient to identify an open menu;
- panel, state and selection at 0x800B25BC/25C0/25C8;
- ten slots at 0x800B10AC, four bytes per slot.

The slot count follows the original draw loop at 0x80049B20, which compares
against ten. Tracing does not change these values.

## Corrected FILE diagnosis

The earlier route with Right then Square selected FILE, not ITEM. A separate
Right-only capture shows FILE highlighted. The original routine at 0x8004C374
slides all panels out in 25 steps, waits for Cross/R1, then restores them.
A regression executes that generated routine and verifies the exact movement,
wait and restoration. The observed positions match it. No port rendering
defect has been established for that behavior.

The actual ITEM route uses Square with ITEM still selected. It renders the
item cursor and SIG P228 description correctly at
`out/runs/inventory-item-command`. Equipment, use and combination are still
being verified. FILE content completeness remains a separate question.

## Equipment change

The route at `out/runs/inventory-equip-second` selects slot 1 and equips
weapon 8 in place of weapon 4. The ten slot IDs/quantities, health and XYZ
remain unchanged, the new weapon model and sound bank are read, and the game
returns to the room. `tools/Test-InventoryEquip.ps1` passes on that evidence
and rejects a negative copy in which the equipped weapon does not change.

Aimed firing subsequently exposed a missing direct cross-image call target
at 0x80066A1C. The recompiler repair and firing replay now pass; see [weapon-fire.md](weapon-fire.md).
