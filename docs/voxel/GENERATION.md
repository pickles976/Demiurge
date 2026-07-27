# Terrain generation: mountains and plains

How a seed becomes a height. Code: `Common/Voxel/Noise.cs` (the wandering),
`Common/Voxel/TerrainShape.cs` (what it means), `Common/Spline.cs` (the curve),
`ChunkGenerator.GenerateChunk` (the field).

## The one idea

**Noise does not produce height. Noise produces parameters, and a hand-drawn curve turns parameters
into height.**

The old generator was `heightAt = noise * Amplitude + SeaLevel` with a single global amplitude, so
every part of the world was equally hilly. You cannot get plains and mountains out of that by
changing the noise, because the noise's job is to say *where you are* and something else has to
decide *what that place is like*.

This is Minecraft's model reduced to its load-bearing part.

## The parameters

- **Erosion** — the one that matters. A very low frequency field (wavelength 220 voxels) saying how
  flat this region is. Because it is its own noise at its own scale, high-erosion blobs become plains
  and low-erosion blobs become mountain ranges, with no special-casing anywhere. Same formula
  everywhere, different parameter value.
- **Detail** — fbm, the ordinary bumpiness, scaled by how much relief erosion allows.
- **Ridge** — folded noise, `1 - |n|`, added only where erosion says mountains. Plain fbm makes
  rolling blobs; folding creates a crease in a smooth field, and a crease in the parameter becomes a
  ridgeline in the terrain.

Not modelled yet, each separable: continentalness (oceans, shorelines), peaks-and-valleys folding for
position within a range, and a 3D density term — the only one of the three that would buy overhangs.

## Why splines and not a multiply

A multiply maps noise to height proportionally, so every region is equally hilly and every transition
equally gradual: a world of medium hills, no plains, no mountains. A curve can hold a **shelf** — a
wide span of input mapping to one output — and that is where flat regions come from. Put a steep
segment beside the shelf and you get the cliff at the edge of a plateau.

**Flatness is a property of the curve, not of low-amplitude noise.** The noise still wanders across
the shelf; the curve ignores it.

All three splines (`BaseHeight`, `Relief`, `Jaggedness`) have a shelf at the high-erosion end.
`Jaggedness` sits at exactly zero across the whole eroded half, so crests appear on mountains and
never in plains.

## Two library facts that shape `NoiseGen`

`NoiseDotNet`'s `GradientNoise2DFractal`:

1. **Accumulates octaves without normalising.** Output range is the sum of octave amplitudes — 1.875
   for four octaves at persistence 0.5. Skip the division and most of the field lands past the ends of
   every spline, flattening the world into two altitudes.
2. **Increments the seed once per octave.** So field seeds must be spaced further apart than their
   octave counts, or the fields correlate and mountains sit exactly where the detail peaks are.
   Erosion/detail/ridge use 100/200/300.

## Slope, and steep faces as stone

The surface gradient was already implied by the height field — `|∇h| = tan θ`, no new noise needed.
It is central-differenced per column, which is the **only** reason
`NoiseGen.GenerateHeightsForChunk` returns a heightmap padded one column on every side. Index padded
arrays through `ChunkTransforms.PaddedColumnIndexOf`; a padded-vs-unpadded mixup is exactly the bug
class the "never compute a block's world position twice" rule exists to prevent.

`DensityToMaterial` checks slope **before** the grass band, so a steep column is rock all the way
down. Without it a cliff shows a one-voxel diagonal stripe of grass over dirt, because the surface
cuts across columns and each column contributes exactly one grass voxel.

### The threshold is not derived from the movement limit, and that was a real finding

The plan was `GrassLimitDegrees = MaxSlopeDegrees - 10`, so green would mean walkable and grey would
mean it isn't — the player reading the collision rule off the terrain.

**Measured, the steepest column in the whole world is 48.7°.** The 50° walk limit never triggers, so
grass cannot signal a distinction that does not exist. Tying them would have put the threshold at 40°,
where under 1% of the world is rock and the feature is invisible. It is an independent **25°** instead,
chosen against the measured slope histogram, which puts stone at ~6% of the surface band.

Attempting to make terrain steeper by shortening the detail/ridge wavelengths **also mostly failed** —
47° to 48.7°. At persistence 0.5 and lacunarity 2 every octave contributes the same gradient
magnitude, so shortening the base wavelength adds fine detail rather than steepness.

The limit is structural: **a smooth fbm heightmap cannot make cliffs.** Genuinely steep faces need
terracing, a spline applied to slope itself, or a 3D density term. Revisit the derived threshold if
terrain ever gets real cliffs.

## The height budget

`ChunkHeight` is 128. The splines put plains around 37 and peaks at 86 — measured range 34.9 to 86.0.
The old generator spanned 16 voxels of the column, which is why everything looked like gentle hills
whatever the noise did.

`TerrainShape.HighestPossible` / `LowestPossible` are deliberately loose bounds (each spline's extreme
paired with every other's, which no single erosion value produces). That is what makes them a safe
budget check: if those fit the world, every real height does. Tested.

## World size is set by erosion, not by taste

`WorldGen` is 41×41 chunks, 656 voxels across. Erosion's wavelength is what makes a plain a plain, so
at ~220 voxels the world has to be several hundred across or the entire map sits inside one erosion
value and comes out uniformly flat or uniformly mountainous — **correct code that looks like the
feature did not work.**
