# Native keyboard input and host-event failures

The original host polled only the current keyboard state. A short press could
begin and end inside one event pump and never reach a guest pad read.

## Reproduction and repair

Before patch 0009, `out/keyboard-trace.log` recorded Enter down and up at
guest frame 2202, but the delivered pad stayed released.

The host now retains key-down for two completed guest-frame numbers while
continuing to use the real held-key state. This covers a complete guest polling
interval without leaving the key stuck. This affects keyboard input only;
the guest game code, gamepad state and deterministic script semantics are unchanged.

After the fix, `out/keyboard-fixed.log` records:
- Enter down/up at frame 556, delivered Start at 557, release at 558.
- Z down/up at frame 1246, delivered Cross at 1247, release at 1249.
- A second Z at 1957 confirms Leon and reaches STAGE1.

Computer Use observed the title, character selection and rooftop during this
native keyboard sequence. Unit checks cover pulse expiry, refresh, independent
keys and reset.

## Outstanding input limitation

Computer Use's injected Left arrow arrived as `Key.Unknown` (-1), and Silk.NET
ImGui's `TranslateInputKeyToImGuiKey` threw NotImplementedException. That probe
does not verify native arrow-key operation. It must not be described as a pass
or silently mapped to an arbitrary direction.

The host previously swallowed or printed these event exceptions without making
acceptance fail. It now retains the first host exception and includes it in the
aggregate runtime check. The deliberate reproduction at
`out/runs/host-event-negative` completes its duration and writes its evidence,
but correctly returns FAIL and exit 1 solely because the runtime check fails.

The normal 180-frame control at `out/runs/host-errors-baseline` passes. This
input limitation remains open for full native-input certification; directional
gameplay in the first-room gate is driven through the traced PS1 pad interface.

## Checks

The focused suite currently passes 47 checks, including the keyboard pulse and
first-host-error retention cases. Recompilation remains byte-identical across
all ten generated files.
