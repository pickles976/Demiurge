# Voxel data model

Design notes for the chunk storage layer. Decided July 2026, before any of it was written —
so treat code as the authority once it exists, and update this when it diverges.

**Status (2026-07-26): most of this is now built.** `Voxel` is 2 bytes (`sbyte` distance +
`BlockType : byte`), quantized at `Scale = 50`; 16³ sections are the meshing and rendering unit;
the padded scratch buffer exists and is cubic. Still unbuilt: uniform-section collapsing in
*storage* (the wire already does it, see `ChunkWire`), per-section palettes, and `WorldMinY` moving
off zero. Server authority is **resolved and not what this doc guessed** — the server serializes and
streams voxels rather than sending a seed, because terrain stops being a pure function of the seed
at the first player edit. See `ChunkWire` / `ChunkStreamer`.

Scope: what we store per voxel and why. Meshing and rendering are deliberately out of scope;
the whole point of this layout is that the data layer doesn't know a renderer exists.

**How to read this.** The first four sections are *concepts* — why density rather than block
types, where density comes from, how material derives from it. Those apply from the first line of
code you write. Everything from "Chunk dimensions" onward is *system* material: a plausible
destination, recorded so the reasoning exists when you get there. **It is not a checklist and not
the next step.** `TODO.md` scopes the next step, and says what it deliberately leaves out.

## Why not just block types

The plan is a *dual* method for surface extraction — [surface
nets](https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14)
first, with [dual contouring](https://www.boristhebrave.com/2018/04/15/dual-contouring-tutorial/)
as a later swap of the vertex placement rule (`MESHING.md`). Neither can run on a `BlockType`
enum. From the DC tutorial: *"Not only do we need to know the value of f(x), we also need to know
the gradient f'(x)."* Surface nets needs only the value for placement, but still needs the
gradient for normals — so the storage argument below is the same either way.

You can derive a density field from materials — air `+1`, solid `-1` — but it's **binary**, and
binary throws away the thing DC needs: where between two grid points the surface actually sits.

DC finds the isosurface by interpolating to zero along each cell edge:

| | corner A (solid) | corner B (air) | crossing |
|---|---|---|---|
| Real density | −0.2 | +0.8 | t = 0.2 |
| Derived from material | −1 | +1 | **t = 0.5, always** |

With a binary field every crossing lands at an edge midpoint, so vertices are locked to a
half-voxel lattice. A hill with a 5% grade — one voxel of rise over twenty of run — comes out
flat for twenty voxels then steps up one. A staircase.

The gradient degrades the same way: central differences on a binary field give each component a
value in {−½, 0, +½}, so normals snap to 26 directions.

This is the entire difference between smooth voxel terrain and Minecraft, and it is a legitimate
fork in the road. **Choosing a dual method is what forces a density field.** If we ever want the
blocky look instead, binary occupancy is correct and greedy meshing replaces the mesher.

## What we store

```csharp
struct Voxel {          // 2 bytes
    sbyte Density;      // quantized signed distance: <0 solid, >=0 air
    BlockType Material; // what it's made of; meaningless where Density >= 0
}
```

Same shape as [bonsairobo's smooth voxel mapping](https://bonsairobo.medium.com/smooth-voxel-mapping-a-technical-deep-dive-on-real-time-surface-nets-and-texturing-ef06d0f8ca14):
`i8` signed distance plus one byte of material index.

**Normals are not stored.** Derive them from the density field by central differences at mesh
time — storing them would triple memory for something reconstructable.

**Material must be derived from the STORED distance, not the pre-quantization one**, or the two
disagree in a quantization-wide band around each whole-number height and the surface picks up
patches of the wrong material. `DensityToMaterial` takes both: existence and the grass band come
from the stored value (what the mesher reads), the deeper bands from the true distance (which the
±2.54-voxel clamp would otherwise collapse).

## Where density comes from

It's already in the noise. `GenerateNoiseForChunk` returns a **continuous** float per column;
today that float only positions a cube. Density is the same number reinterpreted as a field:

```
density(x, y, z) = y - height(x, z)
```

Negative below terrain, positive above, zero at the surface. The sub-voxel precision isn't new
information — it's the fractional part we currently discard by snapping to a block.

With `height(x,z) = 12.37`: density at y=12 is −0.37, at y=13 is +0.63, so DC interpolates
`t = 0.37/(0.37+0.63) = 0.37` and puts the surface at 12.37. Exactly where the noise said.

Quantize with bonsairobo's constant — ~±2.5 voxels of range at 0.02 resolution, which is all
that matters near an isosurface:

```csharp
const float DensityScale = 50f;
Density = (sbyte)Math.Clamp(MathF.Round(distance * DensityScale), -127, 127);
```

Deep voxels clamp to −127 and high ones to +127. Harmless: DC only looks at cells whose corners
disagree in sign.

### Known approximation

`y - height` is *vertical* distance, not true distance to the surface. On a slope it
overestimates, biasing DC vertices downhill. The standard correction divides by the gradient
magnitude:

```
density = (y - height(x,z)) / sqrt(1 + |∇height|²)
```

with `∇height` from finite differences against neighbouring columns. Negligible at current
amplitude; do it when we have cliffs.

### When caves arrive

Density stops being heightmap-derived and becomes genuinely 3D:

```
density(x,y,z) = (y - height(x,z)) + caveNoise3D(x,y,z) * amplitude
```

**The storage format does not change** — same `sbyte`, different generator. That's the reason to
store density now, while terrain is still a heightmap: no migration later.

## Density and material are not peers

The mistake is treating these as two independent noise functions producing two independent
fields. Then nothing stops a Stone voxel from having density 0, and the two disagree about
whether anything is there.

They're a hierarchy. **Density is the authority on what exists. Material only labels what density
already declared solid.** One invariant:

```
Density >= 0  ⟺  Material == Air
```

Nothing in the data structure enforces it. The *generator* enforces it, trivially, because
material generation takes density as its input:

```csharp
static BlockType MaterialFor(float distance)   // distance = y - height
{
    if (distance >= 0f) return BlockType.Air;  // the invariant, in one place

    float depth = -distance;
    if (depth < 1f) return BlockType.Grass;
    if (depth < 4f) return BlockType.Dirt;
    return BlockType.Stone;
}
```

There is no second noise function producing geometry. There is one field, and a classifier that
labels its interior.

### Veins modify the classifier, never the field

```csharp
if (voxel.Density < 0 && VeinNoise3D(worldX, worldY, worldZ) > 0.8f)
    voxel.Material = BlockType.GoldOre;
```

`Density < 0` is the entire coupling. A vein can only *relabel* rock that already exists; where
vein noise says gold but density says air, nothing happens and the vein is clipped to the
terrain. Gold cannot perturb the surface because the vein pass never writes `Density` — and that
is one line to verify, rather than a property of a lookup table someone has to keep disciplined.

This is also why we don't infer density from material. Doing so makes geometry a function of
material *by construction*: give gold a different density from stone and the isosurface moves
wherever gold appears, silently turning the vein pass into a terrain-editing pass.

### The zero case doesn't bite

"Stone with density exactly 0" can't happen because `>= 0` is Air by definition. And it wouldn't
matter: DC emits a vertex only for cells with a sign change, so every cell producing geometry has
at least one solid corner to read a material from. There is no cell that needs a material and has
only air.

## Chunk dimensions

> System-level from here down — a destination, not a next step. A flat `Voxel[ChunkVolume]` per
> chunk is fine until there's a mesher to feed.

**`ChunkIndex` stays 2D.** Vertical complexity is expected to stay low, and the 2D coordinate
maths is already written and tested (`Common/Voxel/ChunkTransforms.cs`, `Common.Tests/`).

- **16 × 16 × 128**, split into **16³ sections** internally (the Minecraft model: 2D-indexed
  column, sections as the storage and meshing unit).
- Sections mean an edit re-meshes 4,096 voxels rather than 32,768, uniform sections (all air, all
  stone — most of them) collapse to a single value, and a 16³ section at 2 bytes is **8 KB, which
  fits in L1**.
- 128 not 256: at 2 bytes that's 64 KB/chunk raw, and `LOAD_DISTANCE` 150 spans ~19 chunks per
  axis ≈ 361 chunks ≈ 23 MB, versus 46 MB. Current terrain relief is ~2 voxels, so 256 buys
  nothing. Height is a constant behind the section abstraction; changing it later is one line and
  a world regen.
- Add a `WorldMinY` constant now so the floor can drop for caves without touching indexing.

### Index layout

```csharp
// y*256 + z*16 + x
public static int VoxelIndex(int x, int y, int z)
    => (y * ChunkWidth * ChunkWidth) + (z * ChunkWidth) + x;
```

Chosen because **the existing 2D formula `z*16 + x` is exactly the y = 0 slice of it** — the
tested coordinate maths extends rather than being replaced.

**Morton / Z-order encoding: no.** It helps random 3D-local access; meshing sweeps the volume in
a known order, where a linear layout is already sequential and prefetcher-friendly, and morton
adds encode/decode on every access. More decisively, a 16³ section is L1-resident, and layout
stops mattering once the working set fits in L1. Sections are the cache win, not morton.

## Padding: unpadded storage, padded scratch buffer at mesh time

Meshing 16 cells needs 17 grid points per axis — the last plane is shared with the neighbour. Two
ways to get it.

**Padded storage (17×17×129).** Meshing is self-contained: one array in, mesh out. But every
chunk duplicates its neighbours' boundary planes, and a single edited voxel on a chunk corner must
then be written to up to 8 chunks. That sync bug shows up as seams that appear only after digging.
It also breaks power-of-two indexing (multiplies by 17 and 289) and costs ~14% more memory. And
+1 isn't even enough: central differences for normals need a neighbour on *both* sides, so true
self-containment wants 18×18×130.

**Unpadded storage (16×16×128), read across chunks at mesh time.** Single source of truth — an
edit writes exactly one place. Power-of-two indexing survives. Cost: meshing a chunk requires its
neighbours loaded, and boundary samples go through an accessor.

**Decision: store unpadded, and copy into a padded thread-local scratch buffer when meshing.**
This is what most engines land on and it gets both halves: unambiguous edits in storage,
self-contained cache-friendly parallel meshing in the scratch copy. The copy is ~74 KB and only
happens on re-mesh. Size the scratch buffer to whatever the mesher needs, independent of storage.

Consequence: design the accessor as `ChunkMap.GetVoxel(worldX, worldY, worldZ)` — world
coordinates, crossing chunk boundaries transparently — from day one.

There's a tempting shortcut where the apron is filled by re-evaluating the noise, since terrain is
a pure function of seed and position. It works until a player digs, then you get seams. Don't
build on it.

## Open

- Sea level, amplitude and `WorldMinY` are unpicked. Noise currently returns ~[−1, 1] and the
  Rust's `NOISE_AMPLITUDE * STEP_SIZE` works out to 1.0, so terrain has ~2 voxels of relief —
  an amplitude multiplier is needed before any of this is visible.
- Server authority. Terrain is a pure function of `(seed, position)`, so the server needs to send
  only the seed plus modifications, not chunks. That decides whether `TerrainChunk` ever needs a
  wire serialiser. Unresolved.
- Material palette compression. `BlockType` is effectively a global palette already; per-section
  palettes are a later optimization.
