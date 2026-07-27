# Terrain collision

Server-authoritative, shared in `Common` so the client predicts with the same code. Files:
`Common/Voxel/TerrainCollision.cs` (field queries) and `Common/PlayerMovement.cs` (the kinematic step).

## The concept that makes it work

**A true signed distance field has unit gradient everywhere.** `d(p)` is the distance to the nearest
surface, so walking one metre toward the surface drops `d` by exactly one metre, and `|∇d| = 1`. That
is what makes `d` usable as "how far out do I push".

**Ours is not that.** `ChunkGenerator` stores `y - heightAt` — the *vertical* gap to the terrain height
in that column, not the distance to the nearest point on the surface. On a 45° slope, a point 1 voxel
above the surface vertically is only 0.707 away perpendicular:

```
        y
        │        ╱  surface h(x) = x
      1 ├── P   ╱
        │  ╲   ╱      stored d = y - h(x) = 1
        │   ╲ ╱       true   d = 1/√2   ≈ 0.707
        │    ╳
        └─────────── x
```

The overestimate is exactly `1/cos θ`, which is also `|∇d|`, since `∇(y - h) = (-∂h/∂x, 1, -∂h/∂z)`.
So **`d / |∇d|` recovers a true distance**, and that division is the single most important line in
`TrySample`. Without it, resolving a sphere until the stored value equals its radius leaves it *sunk
into* the slope by `radius · (1 - cos θ)` — 29% of the radius at 45°, half at 60°.

Note the direction: it sinks in, it does not float. That is easy to get backwards.

## Why the mesher never needed this

Dual contouring asks the field two things only, and both survive:

- **Sign**, which is correct everywhere, so the zero-crossing surface is exactly `y = h(x,z)`.
- **Gradient direction**, which for `y - h` is already the true surface normal. Only its *magnitude*
  was ever wrong, and the mesher normalizes it.

Along a vertical edge `y - h` is perfectly linear in `y`, so the mesher's crossing lerp is exact rather
than an approximation. The field is a perfectly good isosurface and a bad distance function; rendering
needs the former, penetration resolution is the first consumer that needs the latter.

## Two other places the field departs from a true distance

- **The quantization clamp.** `Voxel` saturates around ±2.54 voxels, so deep inside terrain every
  sample reads the same value and the gradient goes to *zero* — no magnitude and no direction. That is
  the "buried" case, and it needs a defined fallback (push straight up) or a player inside a hill is
  stuck permanently. It is also why collision sub-steps must stay inside that band: displace further
  than ~2 voxels in one step and the body passes clean through the ground however wide it is.
- **CSG edits.** `min` for add and `max(d, -shape)` for subtract preserve sign exactly but not
  magnitude — `max` of two distance fields is only a bound. So even a hand-authored true SDF stops
  being one the first time somebody digs.

## Shape of the step

Capsule as three sample spheres up its axis. `PlayerMovement.Step` does: horizontal velocity from
intent → jump → gravity → sub-stepped collide-and-slide → ground probe. Pushout iterates because the
field is not a true distance function, so one push along the gradient lands close rather than exact.

Position is the **feet**, matching `SurfaceQuery.SurfacePosition` and what the view renders from.

`MoveState` (position, velocity, grounded) travels on the wire in `PlayerPositionData`. Velocity has to:
reconciliation replays pending moves from authoritative state, so snapping position while keeping local
velocity diverges on the first tick after every correction and compounds from there.

**Unloaded terrain is impassable**, which doubles as the world edge. On the client that would wall the
player in place at spawn, so `LocalPlayer` suspends prediction while `TerrainState.FootprintLoaded` is
false and follows authority instead — while still *sending* input, since the server keeps stepping it.

## Two bugs the tests caught

**Walking uphill launched the player.** The slide projection leaves *upward* velocity after a tick on a
slope. Carried into the next tick it reads as a jump, so `Grounded` flickered and the body ratcheted off
every hill. Fixed in two places: a grounded body carries no vertical momentum at all (both signs, not
just downward), and a standable contact clamps `Velocity.Y ≤ 0` — climbing is the *pushout's* job.

**A 1.5 m jump only reached 1.37 m.** Gravity is applied before the displacement each tick
(semi-implicit Euler), so the whole flight integrates with velocities half a tick too low. At 30 Hz
that is exactly the difference between clearing a 1.5 m ledge and bouncing off it. `JumpSpeed` adds
back `g·dt/2` so the *discrete* apex matches `JumpHeight`.

## Known limit

Past about 45° the gradient stencil reaches grid points beyond the ±2.54 clamp, so the measured slope
comes out shallower than reality and the corrected distance is biased **long** — the body sits slightly
into the face. At 60° that is 0.54 against a true 0.5, versus 1.0 uncorrected. Anything steeper than
`MaxSlopeDegrees` is flattened into a wall anyway, so the exact tilt stops mattering. Recorded as a test
rather than a comment.
