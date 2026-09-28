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
- menu-open bit 0x40 at 0x800ACA3C;
- panel, state and selection at 0x800B25BC/25C0/25C8;
- ten slots at 0x800B10AC, four bytes per slot.

The slot count follows the original draw loop at 0x80049B20, which compares
against ten. Tracing does not change these values.

## Open rendering issue

Reproduction:
`90:start,240:cross,300:cross,600:right,1250:cross,1800:start,2100:right,2160:square`,
stopped at frame 2500.

Evidence: `out/runs/elza-inventory-selection`.

The guest changes panel from 0 to 3, keeps the inventory open, and leaves the
selected slot and quantities unchanged. Visually, most menu content moves out
of view, leaving the frame and a clipped strip on the left. The native runtime
reports no exception or unmapped call. A smoke pass therefore does not close
this UI issue.

The cause has not yet been attributed to the port or original prototype.
Item selection, equipping, using, combining, and persistence are not accepted.
Do not classify the rendering issue as an original limitation without evidence.
