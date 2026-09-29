# Scripted input timing

How a schedule turns into guest movement, measured rather than assumed. Every figure
here comes from the host's own per-frame state trace (`--sample 0.03` records a
sample every guest frame instead of every 15), and the runs are reproducible from the
commands at the end.

## Turning

A `right` or `left` entry at frame F with duration D rotates by exactly **96 x (D - 1)**
yaw units. The rotation is applied on frames **F+2 through F+D**, at exactly **96 units
per frame**.

- There is no ramp. The first increment is the same size as the 63rd: 228 measured
  increments across holds of 2..64 frames, in both directions, are `+/-96` without
  exception, and repeated runs are bit-identical.
- A **1-frame hold rotates by zero**. A schedule that relies on one is a no-op.
- So to rotate by D units, hold for `D / 96 + 1` frames, and D must be a multiple of
  96. A full 4096-unit turn is not achievable in a single hold.
- Yaw wraps per frame, modulo 4096.
- `left` and `right` never shift x or z: 0 changes across 2120 samples in 20 turn-only
  runs.

This corrects an earlier guess that the turn rate ramps with hold length. It was
inferred from two measurements and is simply wrong: the rate is constant.

## Moving

Forward direction follows

    direction = ( cos(yaw * 2*pi/4096), -sin(yaw * 2*pi/4096) )

confirmed at four headings (1192, 232, 3016, 3464) to within +/-0.71 degrees.

Speed is **not constant**, and this matters for any distance arithmetic:

- Per-frame displacement oscillates between **52.89 and 82.71 units** per frame in
  steady state, reaching **94.11** during an 8-frame start-up transient.
- The oscillation has a strict **34-frame period**.
- The converged mean is **73.63 units per frame**. Quoting ~73 is fine as a mean and
  misleading as a per-frame figure.

Onset latency differs by input: `up` acts on frame F+1, a turn acts on F+2.

## Collision

Walls do not stop the character, they **slide** it along the free axis, at that axis's
intended velocity component. Consequently "the player moved" is not evidence that a
walk went where it was aimed, and a position that does not change on one axis is not
evidence that the character is stuck.

A walk at yaw 1192 from the rooftop start travelled 17712 units to `(-645,-2942)`,
crossing x=0 and z=0, with no clamping on the way. Walls were found at z=14578 and
x=10240. A separate run at yaw 1000 stopped advancing in z at **-6000** while still
sliding slightly in x, which is the boundary that makes room 117's door-1 rectangle
(z -9400..-6480) unwalkable from the rooftop - see `content-coverage.md`.

## An unresolved disagreement, recorded rather than smoothed over

The law above was measured from **standstill, in open space**. It does not reproduce
one observation made while the character was pinned against the z=-6000 wall: a
34-frame `right` hold there produced **+1440** yaw units, not the 3168 the law
predicts - 42 units per frame instead of 96.

Neither result has been shown wrong. The likely explanation is that turning is
restricted while colliding, which would make the law conditional on contact, but that
has not been tested. Until it is, the 96 units per frame figure should be treated as
confirmed for free-standing turns and unknown for turns taken against geometry. This
is recorded because the alternative - quietly adopting the tidier number - would hide
a real gap in the model.

## Not established

- Whether speed depends on heading. Yaw 1192 measures 73.63/frame against yaw 232 at
  72.88/frame; the 1% gap is the same order as per-axis integer rounding bias and
  cannot be separated from this data.
- Whether the 8-frame start-up transient is heading-independent. It is plainly
  different at yaw 232, so the distance table derived at yaw 1192 must not be assumed
  exact immediately after a turn.
- Turns taken while already moving. Every turn measured here started from standstill.
- Holds longer than 64 frames.
- Reachable-heading granularity. `gcd(96,4096) = 32` implies 128 distinct headings, but
  that is derived from the measured increment rather than observed directly.
- Rooftop geometry beyond the walls listed above. One character, one room, one loadout.

## Reproducing

```powershell
# one measurement: turn right for 16 frames from the standing rooftop state
dotnet port\RE15pc\bin\Release\net10.0\RE15pc.dll --frames 1900 --timeout 240 --sample 0.03 `
  --input "90:start,240:cross,300:cross,600:up:600,1250:cross,1800:right:16" --out out\turn-r16

# states.json then holds one sample per guest frame: frame, player.x/y/z/yaw
```

The full sweep is 34 runs; per-run CSVs and the raw host output are local under
`out/turn-calib-series/` and `out/turn-calib-*/`, and are not committed for the same
reason the rest of `out/` is not.
