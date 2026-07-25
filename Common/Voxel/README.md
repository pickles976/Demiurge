# Voxel coordinate systems

Stride's world space is **right-handed, Y-up, forward = −Z, X is right.** Everything below
inherits that: the ground plane is X/Z and Y is always height *in world space*.

There are five spaces. Going from a point in the world down to a slot in an array means passing
through all of them, in this order:

```
World position  →  Global block coords  →  Chunk index  →  Local voxel coords  →  Voxel index
  Vector3            integer, whole world    which chunk      inside that chunk      array slot
  (20.3, 5.7, -3.2)  (20, 5, -4)             {x:1, y:-1}      {x:4, y:5, z:12}       1476
```

## The naming trap

`ChunkIndex.y` and `BlockCoords.y` mean **world Z**. `LocalVoxelCoords.y` means **height**.

That inconsistency is inherited from the 2D heightmap era, where there was no height axis to
compete for the name. It is the single easiest thing to get wrong in this code — a shipped bug
came from reading `position.Y` where `position.Z` was meant, and it only showed up once terrain
left the origin chunk.

| Type | `.x` | `.y` | `.z` |
|---|---|---|---|
| `ChunkIndex` | chunk X | chunk **Z** | — |
| `BlockCoords` | local X | local **Z** | — |
| `LocalVoxelCoords` | local X | **height** | local Z |

When you need the ground plane from a `Vector3`, go through
`ConvertVector2ToChunkCoordinates(new Vector2(position.X, position.Z))` rather than picking
fields by hand — that projection exists to make the choice of axis explicit.

## The five spaces

### 1. World position — `Vector3`, float

Stride's space. What a `TransformComponent` holds. Continuous, unbounded, can be negative.

### 2. Global block coordinates — integer, whole world

A single block's cell, numbered continuously across all chunks. Pinned to the cell's
**bottom-left corner**, so a block owns `[position, position + TileSize)` on each axis.

A cube mesh is centred on its origin, so rendering one adds half a tile:
`position + TileSize / 2`.

### 3. Chunk index — `ChunkIndex`, 2D

Which chunk, on the ground plane only. **There is no vertical chunking** — one chunk is a full
16 × 16 × 128 column. Pinned to the chunk's bottom-left corner, so chunk *n* spans
`[n * 16, n * 16 + 16)`.

Derived by **floor division**, not truncation: `floor(worldX / 16)`. Truncation rounds toward
zero, which puts world X = −16 in chunk −2 when it is the first column of chunk −1.

### 4. Local voxel coordinates — `LocalVoxelCoords`

Where a voxel sits inside its own chunk. `x` and `z` are 0..15, `y` is 0..127 and is height.

Because there is no vertical chunking, **local y is world y**. That equivalence is the reason
`ConvertChunkAndVoxelIndexToGlobalBlockPosition` can pass `local.y` straight through. It stops
being true the day chunks gain a Y index.

World Y is unbounded but a chunk is not, so a position converted from the world can land outside
vertically. `IsInsideChunk` before indexing.

### 5. Voxel index — `int`

A slot in the chunk's flat `Voxel[ChunkVolume]` array.

```
index = y * 256 + z * 16 + x        (y * ChunkSize + z * ChunkWidth + x)
```

Chosen so **the 2D column formula `z * 16 + x` is exactly the y = 0 slice**. Two consequences
worth knowing, because they make heightmap code fall out for free:

- `index % 256` → the column index, i.e. the slot in a 256-entry heightmap. The `y` term is a
  multiple of 256, so the modulo strips it and leaves `z * 16 + x`.
- `index / 256` → the local height `y`.

So when filling a chunk from a heightmap, iterating the flat array once is enough — no nested
loop needed:

```csharp
for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
{
    float heightAt = heightmap[ChunkTransforms.GetColumnIndexFromVoxelIndex(i)];
    int y = ChunkTransforms.GetLocalYFromVoxelIndex(i);
    float distance = y - heightAt;      // <0 solid, >=0 air
    ...
}
```

## Conversions

| From → To | Function |
|---|---|
| World → chunk index | `ConvertVector3ToChunkCoordinates` |
| World (ground only) → chunk index | `ConvertVector2ToChunkCoordinates` |
| World → local column coords | `ConvertVector3ToLocalBlockCoordinates` |
| World → local voxel coords | `ConvertVector3ToLocalVoxelCoordinates` |
| Chunk index → world origin | `ConvertChunkCoordinatesToVector3` |
| Chunk index → world extents | `GetChunkCornersInWorldSpace` |
| Chunk index + column index → global block coords | `ConvertChunkCoordinatesAndBlockIndexToGlobalBlockCoordinates` |
| Chunk index + voxel index → world position | `ConvertChunkAndVoxelIndexToGlobalBlockPosition` |
| Local voxel coords → voxel index | `GetVoxelIndexFromLocalBlockCoordinates` / `...FromLocalVoxelCoordinates` |
| Voxel index → local voxel coords | `GetLocalVoxelCoordinatesFromVoxelIndex` |
| Voxel index → column index | `GetColumnIndexFromVoxelIndex` |
| Voxel index → local height | `GetLocalYFromVoxelIndex` |
| Local column coords → column index | `ConvertLocalBlockCoordsToBlockIndex` |
| Camera position + radius → chunk indices | `GetIndicesFromCenterAndDistance` |

Test vectors for most of these live in `Common.Tests/CoordinateVectorTests.cs`, ported from the
Rust original at `/home/sebas/Projects/Demiurge`.

## Worked example

World position `(20.3, 5.7, -3.2)`:

| Step | Result | How |
|---|---|---|
| Global block coords | `(20, 5, -4)` | floor each axis |
| Chunk index | `{x: 1, y: -1}` | `floor(20/16) = 1`, `floor(-4/16) = -1` |
| Chunk origin | `(16, 0, -16)` | `index * 16` |
| Local voxel coords | `{x: 4, y: 5, z: 12}` | position − origin |
| Voxel index | `1476` | `5*256 + 12*16 + 4` |

Note the chunk index is −1 on the Z axis and the local Z is 12, not −4: local coordinates are
always measured from the chunk's own bottom-left corner, so they are never negative.
