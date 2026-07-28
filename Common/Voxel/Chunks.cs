using System.Collections.Concurrent;

namespace Demiurge
{


    public static class ChunkConstants {
        public const int ChunkWidth = 16;
        public const int ChunkHeight = 128;
        public const int ChunkSize = ChunkWidth * ChunkWidth;
        public const int ChunkVolume = ChunkWidth * ChunkWidth * ChunkHeight;

        /// <summary>Edge length of one block in world units.</summary>
        public const float TileSize = 1.0f;

        /// <summary>
        /// A chunk's column is meshed and drawn in cubic SECTIONS. Storage is still one flat array
        /// per chunk — a section is a view into it, not a separate allocation — so nothing about
        /// indexing, edits or ChunkIndex changes. What it buys is that an edit re-meshes 16^3 voxels
        /// instead of the whole 16x16x128 column, and each section frustum-culls on its own tight box
        /// rather than the whole column drawing whenever any of it is visible.
        /// </summary>
        public const int SectionHeight = 16;
        public const int SectionsPerChunk = ChunkHeight / SectionHeight;   // 8
        /// <summary>
        /// The world's vertical extent, [WorldMinY, WorldMaxY). ChunkIndex is 2D, so one chunk
        /// spans the full height and no chunk will ever load outside this range.
        /// </summary>
        public const int WorldMinY = 0;
        public const int WorldMaxY = WorldMinY + ChunkHeight;

        /// <summary>
        /// The bottom voxel plane is permanently solid, and nothing may write air into it.
        ///
        /// Two reasons, one invariant. Gameplay: you cannot dig out of the world. Rendering: the
        /// lowest grid point any section OWNS is WorldMinY, so a sign change between WorldMinY-1 and
        /// WorldMinY sits on an edge nobody emits a quad for — carve the floor away and you get a
        /// hole you can see through rather than a visible bottom. Keeping this plane solid moves the
        /// lowest possible sign change up to WorldMinY..WorldMinY+1, which is owned and drawn.
        ///
        /// Enforced where voxels are WRITTEN (generation and edits), never at mesh time: patching it
        /// in the mesher would leave the data holed, so a dig or a raycast would disagree with what
        /// you can see.
        /// </summary>
        public const int BedrockThickness = 1;

        /// <summary>
        /// Distance forced into the bedrock plane: solid, and far enough from zero that no
        /// interpolation puts a surface inside it.
        /// </summary>
        public const float BedrockDistance = -1f;

        public static bool IsBedrock(int worldY) => worldY < WorldMinY + BedrockThickness;

        /// <summary>
        /// The world-floor invariant, in one expression that every voxel write goes through. Takes
        /// the distance a generator or an edit wanted and returns what it's allowed to store.
        /// </summary>
        public static float ClampToWorldFloor(int worldY, float distance)
            => IsBedrock(worldY) ? MathF.Min(distance, BedrockDistance) : distance;
    }

    /// <summary>A chunk's position on the ground plane. See ChunkTransforms.cs for conventions.</summary>
    public struct ChunkIndex : IEquatable<ChunkIndex>
    {
        public int x;
        public int z;

        // IEquatable is NOT optional here. Without it Dictionary falls back to
        // ObjectEqualityComparer -> ValueType.Equals(object), which boxes both operands and compares
        // by reflection. Meshing does ~58k lookups per chunk, so that cost 3.9 MB of garbage and 80%
        // of the re-mesh time.
        public bool Equals(ChunkIndex other) => x == other.x && z == other.z;
        public override bool Equals(object? obj) => obj is ChunkIndex other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, z);
        public override string ToString() => $"({x}, {z})";
    }

    /// <summary>
    /// One cubic slice of a chunk's column: <paramref name="x"/> and <paramref name="z"/> are chunk
    /// coordinates, <paramref name="y"/> is the section 0..SectionsPerChunk-1 counting up from
    /// WorldMinY. A record struct so it gets IEquatable and GetHashCode for free — see the note on
    /// <see cref="ChunkIndex"/> for what happens to a dictionary key without them.
    /// </summary>
    public readonly record struct SectionIndex(int x, int y, int z)
    {
        public ChunkIndex Chunk => new() { x = x, z = z };

        /// <summary>World Y of this section's bottom voxel plane.</summary>
        public int BaseY => ChunkConstants.WorldMinY + y * ChunkConstants.SectionHeight;

        public static SectionIndex Of(ChunkIndex chunk, int sectionY) => new(chunk.x, sectionY, chunk.z);
    }

    public class ChunkMap
    {
        /// <summary>
        /// Concurrent because MESHING READS THIS FROM WORKER THREADS while the main thread inserts
        /// arriving chunks. A plain Dictionary insert racing a lookup corrupts the buckets or throws,
        /// rather than merely returning stale data — the same hazard that made
        /// <see cref="GameClient.TerrainState"/> queue arrivals instead of applying them on the network
        /// thread.
        ///
        /// This only makes the LOOKUP safe. A chunk's voxel array is still mutable while its slabs are
        /// arriving, so callers that read voxels off the main thread must first establish that the chunk
        /// is complete — see TerrainState.NeighbourhoodComplete.
        ///
        /// Meshing does ~441 lookups per section (one per apron column, not one per voxel), so the extra
        /// cost against a plain Dictionary is irrelevant here.
        /// </summary>
        readonly ConcurrentDictionary<ChunkIndex, TerrainChunk> chunks = new();

        public void Reset() { this.chunks.Clear(); }

        public bool Has(ChunkIndex index)
        {
            return this.chunks.ContainsKey(index);
        }
        
        public TerrainChunk? Get(ChunkIndex index) => chunks.TryGetValue(index, out var chunk) ? chunk : null;

        /// <summary>
        /// A voxel by WORLD coordinates, crossing chunk boundaries transparently. false means "no
        /// data", never "air" — air beside solid is a sign change, so substituting it would make
        /// the mesher emit a wall along the chunk boundary.
        /// </summary>
        public bool TryGetVoxel(int worldX, int worldY, int worldZ, out Voxel voxel)
        {
            voxel = default;

            if (worldY < ChunkConstants.WorldMinY)  { voxel = Voxel.OutsideBelow; return true; }
            if (worldY >= ChunkConstants.WorldMaxY) { voxel = Voxel.OutsideAbove; return true; }

            TerrainChunk? chunk = Get(ChunkTransforms.ChunkAt(worldX, worldZ));
            if (chunk == null) return false;

            int voxelIndex = ChunkTransforms.WorldVoxelIndex(worldX, worldY, worldZ);

            voxel = chunk[voxelIndex];
            return true;
        }

        public void Insert(TerrainChunk chunk)
        {
            chunks[chunk.index] = chunk;
        }

    }

    public class ChunkGenerator
    {
        /// <summary>
        /// Where grass stops and bare rock starts, in degrees of surface slope.
        ///
        /// This is deliberately the same number as <see cref="PlayerMovement.MaxSlopeDegrees"/>: grass
        /// means walkable soil, bare stone means the slope is too steep to climb by ordinary movement.
        /// </summary>
        public const float GrassLimitDegrees = PlayerMovement.MaxSlopeDegrees;

        /// <summary>The same limit as a gradient magnitude, which is what a heightmap slope measures.</summary>
        public static readonly float GrassLimitSlope = MathF.Tan(GrassLimitDegrees * (MathF.PI / 180f));

        public static TerrainChunk GenerateChunk(ChunkIndex index)
        {
            float[] heights = NoiseGen.GenerateHeightsForChunk(index);   // padded, world units
            float[] slopes = ColumnSlopes(heights);

            TerrainChunk chunk = new TerrainChunk(index);

            // One slab at a time, into a buffer that is only committed if the slab turns out mixed.
            // Writing voxel by voxel would materialise all 128 slabs and then free ~120 of them, which
            // measured at 300 MB of transient garbage across the map.
            var slab = new Voxel[ChunkConstants.ChunkSize];

            for (int slabY = 0; slabY < ChunkConstants.ChunkHeight; slabY++)
            {
                int worldY = ChunkConstants.WorldMinY + slabY;
                bool uniform = true;

                for (int column = 0; column < ChunkConstants.ChunkSize; column++)
                {
                    int i = slabY * ChunkConstants.ChunkSize + column;

                    float heightAt = heights[ChunkTransforms.PaddedColumnIndexOf(i)];
                    // Through the world-floor clamp like every other voxel write: noise can put a
                    // column's height at or below WorldMinY, which would otherwise generate a hole.
                    float distance = ChunkConstants.ClampToWorldFloor(worldY, slabY - heightAt);

                    // Store first, then label from what was actually stored: quantization is what the
                    // mesher will see, so the material has to agree with it rather than with `distance`.
                    var voxel = new Voxel { Distance = distance };
                    voxel.Material = DensityToMaterial(voxel.Distance, distance, slopes[column]);

                    slab[column] = voxel;

                    if (column > 0 && (voxel.Density != slab[0].Density || voxel.Material != slab[0].Material))
                        uniform = false;
                }

                if (uniform) chunk.FillSlab(slabY, slab[0]);
                else slab.AsSpan().CopyTo(chunk.Materialize(slabY));
            }

            return chunk;
        }

        /// <summary>
        /// tan(slope) per column, central-differenced off the PADDED heights — which is the only reason
        /// the heights are padded. No new noise and no extra storage: the surface gradient was already
        /// implied by the height field.
        /// </summary>
        static float[] ColumnSlopes(float[] paddedHeights)
        {
            var slopes = new float[ChunkConstants.ChunkSize];

            for (int z = 0; z < ChunkConstants.ChunkWidth; z++)
            {
                for (int x = 0; x < ChunkConstants.ChunkWidth; x++)
                {
                    float dx = (paddedHeights[ChunkTransforms.PaddedColumnIndex(x + 1, z)]
                              - paddedHeights[ChunkTransforms.PaddedColumnIndex(x - 1, z)]) * 0.5f;

                    float dz = (paddedHeights[ChunkTransforms.PaddedColumnIndex(x, z + 1)]
                              - paddedHeights[ChunkTransforms.PaddedColumnIndex(x, z - 1)]) * 0.5f;

                    slopes[ChunkTransforms.ColumnIndex(x, z)] = MathF.Sqrt(dx * dx + dz * dz);
                }
            }

            return slopes;
        }

        /// <summary>
        /// Labels a voxel. Material is DERIVED from density, never sampled independently — that
        /// one-way dependency is what stops the two fields disagreeing about whether a voxel exists.
        ///
        /// Takes BOTH the stored distance and the one the generator computed, because the two bands
        /// need different authorities and using either alone produces wrong surface materials:
        ///
        /// - Existence and the grass band come from the STORED value, since that's the field the
        ///   mesher reads. Deriving them from the true distance disagrees with the mesher in a
        ///   quantization-wide band around every whole-number height, which is what put stray patches
        ///   of Dirt on the surface.
        /// - The deep bands come from the TRUE distance, because stored distance clamps at about
        ///   -2.54 voxels — deriving Dirt/Stone from it would make everything below that Dirt and
        ///   Stone would never appear at all.
        /// </summary>
        /// <summary>
        /// Slope-free overload, for fields whose surface gradient is zero or unknown — synthetic test
        /// fields and player edits, where the column's slope says nothing about the cut face.
        /// </summary>
        public static BlockType DensityToMaterial(float storedDistance, float trueDistance)
            => DensityToMaterial(storedDistance, trueDistance, slope: 0f);

        /// <summary>
        /// How deep the dirt goes before it becomes stone, in voxels below the surface.
        ///
        /// This is a DIGGING budget as much as a look: bare hands only move soil, so it sets how far
        /// down a player can get before needing a tool. Deep enough to sink a shelter into a hillside.
        /// </summary>
        public const float SoilDepth = 9f;

        /// <inheritdoc cref="DensityToMaterial(float, float)"/>
        /// <param name="slope">tan of the surface angle at this column, from <see cref="ColumnSlopes"/>.</param>
        public static BlockType DensityToMaterial(float storedDistance, float trueDistance, float slope)
        {
            if (storedDistance >= 0f) return BlockType.BlockType_Air;   // the invariant, in one place

            // Too steep to hold soil. Before the grass band rather than inside it, so a cliff is rock all
            // the way down instead of a diagonal stripe of grass over dirt — which is what a heightmap
            // surface cutting across columns would otherwise produce on every mountainside.
            if (slope > GrassLimitSlope) return BlockType.BlockType_Stone;

            // "The topmost solid voxel", expressed in stored terms. That voxel's stored depth always
            // lands in (0, 1] — exactly 1.0 when the voxel above it quantized to air — and the voxel
            // below it is always past 1, so this picks out the surface layer and nothing else.
            if (storedDistance >= -1f) return BlockType.BlockType_Grass;

            float depth = -trueDistance;
            if (depth < SoilDepth) return BlockType.BlockType_Dirt;
            return BlockType.BlockType_Stone;
        }
    }

    /// <summary>
    /// A chunk's voxels, stored as 128 horizontal SLABS that are allocated only when they need to be.
    ///
    /// Most of a column is one repeated voxel — everything above the terrain is air and everything below
    /// is clamped solid — so a flat 32,768-entry array spent 64 KB per chunk to store about 17 slabs of
    /// actual content. At 1 km that was ~254 MB per map, and singleplayer holds two. A null slab means
    /// "every voxel here is <see cref="Uniform"/>", which takes a typical chunk to around 10 KB.
    ///
    /// A class so ChunkMap hands these out by reference. As a struct, writes to the slab array would have
    /// propagated (shared reference) while writes to `index` silently would not.
    /// </summary>
    public class TerrainChunk
    {
        public ChunkIndex index;

        /// <summary>Null entry: that slab is uniform and its value is in <see cref="uniform"/>.</summary>
        readonly Voxel[]?[] slabs = new Voxel[ChunkConstants.ChunkHeight][];
        readonly Voxel[] uniform = new Voxel[ChunkConstants.ChunkHeight];

        public TerrainChunk(ChunkIndex index) => this.index = index;

        /// <summary>
        /// By flat voxel index, the layout everything already computes through
        /// <see cref="ChunkTransforms.WorldVoxelIndex"/>. The divide and mod are by powers of two, so
        /// this costs a shift, a mask, a null check and an indirection over the old array read.
        /// </summary>
        public Voxel this[int flatIndex]
        {
            get
            {
                int slabY = flatIndex / ChunkConstants.ChunkSize;
                var slab = slabs[slabY];

                return slab is null ? uniform[slabY] : slab[flatIndex % ChunkConstants.ChunkSize];
            }
            set
            {
                int slabY = flatIndex / ChunkConstants.ChunkSize;
                Materialize(slabY)[flatIndex % ChunkConstants.ChunkSize] = value;
            }
        }

        public bool IsUniform(int slabY) => slabs[slabY] is null;

        /// <summary>Only meaningful where <see cref="IsUniform"/>.</summary>
        public Voxel UniformValue(int slabY) => uniform[slabY];

        /// <summary>
        /// Collapses a slab to a single value and frees its array. This is how a chunk STAYS small:
        /// decoding knows which slabs are uniform, so it can say so rather than write 256 copies.
        /// </summary>
        public void FillSlab(int slabY, Voxel value)
        {
            slabs[slabY] = null;
            uniform[slabY] = value;
        }

        /// <summary>Backing array for a slab, allocating and expanding the uniform value into it if needed.</summary>
        public Voxel[] Materialize(int slabY)
        {
            var slab = slabs[slabY];
            if (slab is not null) return slab;

            slab = new Voxel[ChunkConstants.ChunkSize];
            slab.AsSpan().Fill(uniform[slabY]);

            return slabs[slabY] = slab;
        }

        /// <summary>
        /// Frees any slab whose voxels all turned out identical. Worth calling after a pass that writes
        /// voxel by voxel — generation does — since that materializes everything on the way through.
        /// </summary>
        public void CollapseUniformSlabs()
        {
            for (int slabY = 0; slabY < ChunkConstants.ChunkHeight; slabY++)
            {
                var slab = slabs[slabY];
                if (slab is null) continue;

                var first = slab[0];
                bool same = true;

                for (int i = 1; i < slab.Length; i++)
                {
                    if (slab[i].Density == first.Density && slab[i].Material == first.Material) continue;

                    same = false;
                    break;
                }

                if (same) FillSlab(slabY, first);
            }
        }

        /// <summary>Slabs currently carrying an array. Diagnostics and tests only.</summary>
        public int AllocatedSlabs
        {
            get
            {
                int count = 0;
                foreach (var slab in slabs) if (slab is not null) count++;
                return count;
            }
        }
    }
}
