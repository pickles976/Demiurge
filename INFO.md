  Reasoning for all of this is in `docs/voxel/DATA_MODEL.md`. Notes for just this step:

  **Constants.** 16 wide, 128 tall. `ChunkVolume = 16*16*128 = 32,768`. Index so that the
  existing tested 2D formula `z*16 + x` is the y=0 slice of it:

  ```csharp
  public static int VoxelIndex(int x, int y, int z)
      => (y * ChunkWidth * ChunkWidth) + (z * ChunkWidth) + x;   // y*256 + z*16 + x
  ```

  **The voxel.** Two fields, 2 bytes. Density is the geometry, material is the label:

  ```csharp
  struct Voxel {
      sbyte Density;       // quantized signed distance: <0 solid, >=0 air
      BlockType Material;  // meaningless where Density >= 0
  }
  ```

  `BlockType` needs an explicit `Air = 0` member — the invariant below is written in terms of
  it, and `BlockType_Default` doesn't say whether it's air or stone.

  **Density comes from the heightmap you already generate.** `density = y - height(x,z)`,
  negative below terrain, positive above, zero at the surface. The value is already a float —
  keeping the fraction is the whole point, since that's what tells DC later that a surface at
  height 12.37 sits 37% of the way up the edge from y=12 to y=13. Quantize with ×50 (≈±2.5
  voxels of range at 0.02 resolution, all that matters near a surface); clamping far values to
  ±127 is fine.

  ```csharp
  for (int z = 0; z < ChunkWidth; z++)
  for (int x = 0; x < ChunkWidth; x++)
  {
      float height = heights[z * ChunkWidth + x] * Amplitude + SeaLevel;

      for (int y = 0; y < ChunkHeight; y++)
      {
          float distance = y - height;              // <0 solid, >=0 air
          voxels[VoxelIndex(x, y, z)] = new Voxel {
              Density  = (sbyte)Math.Clamp(MathF.Round(distance * 50f), -127, 127),
              Material = MaterialFor(distance),
          };
      }
  }
  ```

  Note the noise is still only sampled 256 times per chunk, not 32,768 — a heightmap extrudes
  vertically, the inner loop is just a subtraction.

  **Material is derived from density, never sampled independently.** That one-way dependency is
  what stops the two fields disagreeing about whether a voxel exists:

  ```csharp
  static BlockType MaterialFor(float distance)   // distance = y - height
  {
      if (distance >= 0f) return BlockType.Air;  // the invariant, in one place

      float depth = -distance;                    // how far below the surface
      if (depth < 1f) return BlockType.Grass;
      if (depth < 4f) return BlockType.Dirt;
      return BlockType.Stone;
  }
  ```

  Veins later are a second pass that only ever *relabels* existing rock — the `Density < 0`
  guard is the entire coupling, and it's why gold can't move the surface:

  ```csharp
  if (voxel.Density < 0 && VeinNoise3D(wx, wy, wz) > 0.8f)
      voxel.Material = BlockType.GoldOre;
  ```

  **Turn up the amplitude first.** Noise returns ~[-1, 1] and the Rust's
  `NOISE_AMPLITUDE * STEP_SIZE` works out to 1.0, so terrain currently has ~2 voxels of relief.
  Without an amplitude multiplier (24-ish) and a sea level offset, none of this will look like
  it changed anything.

  Deliberately NOT in this step: 16³ sections and uniform-section collapse, the cross-chunk
  `GetVoxel(worldX, worldY, worldZ)` accessor, padded scratch buffers. A flat
  `Voxel[ChunkVolume]` per chunk is fine until meshing.