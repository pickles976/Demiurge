

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
        public Dictionary<ChunkIndex, TerrainChunk> chunks = new();

        public ChunkMap()
        {
            
        }

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

            voxel = chunk.voxels[voxelIndex];
            return true;
        }

        public void Insert(TerrainChunk chunk)
        {
            chunks[chunk.index] = chunk;
        }

    }

    public class ChunkGenerator
    {

        const float Amplitude = 8f;
        const float SeaLevel  = 8f;

        public static TerrainChunk GenerateChunk(ChunkIndex index)
        {

            float[] heightMap = NoiseGen.GenerateNoiseForChunk(index);
            TerrainChunk chunk = new TerrainChunk(index);

            // Get the density of each voxel from the heightmap
            for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            {
                // Height at X,Z voxel index
                float heightAt = heightMap[ChunkTransforms.ColumnIndexOf(i)] * Amplitude + SeaLevel;  // i % 256
                int y = ChunkTransforms.LocalYOf(i);                           // i / 256
                // Through the world-floor clamp like every other voxel write: noise can put a
                // column's height at or below WorldMinY, which would otherwise generate a hole.
                float distance = ChunkConstants.ClampToWorldFloor(ChunkConstants.WorldMinY + y, y - heightAt);

                // Store first, then label from what was actually stored: quantization is what the
                // mesher will see, so the material has to agree with it rather than with `distance`.
                chunk.voxels[i].Distance = distance;
                chunk.voxels[i].Material = DensityToMaterial(chunk.voxels[i].Distance, distance);
            }

            return chunk;
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
        public static BlockType DensityToMaterial(float storedDistance, float trueDistance)
        {
            if (storedDistance >= 0f) return BlockType.BlockType_Air;   // the invariant, in one place

            // "The topmost solid voxel", expressed in stored terms. That voxel's stored depth always
            // lands in (0, 1] — exactly 1.0 when the voxel above it quantized to air — and the voxel
            // below it is always past 1, so this picks out the surface layer and nothing else.
            if (storedDistance >= -1f) return BlockType.BlockType_Grass;

            float depth = -trueDistance;
            if (depth < 4f) return BlockType.BlockType_Dirt;
            return BlockType.BlockType_Stone;
        }
    }

    // A class so ChunkMap hands these out by reference. As a struct, writes to `voxels` would
    // have propagated (shared array) while writes to `index` silently would not.
    public class TerrainChunk
    {
        public ChunkIndex index;

        public Voxel[] voxels;

        public TerrainChunk(ChunkIndex index)
        {
            this.index = index;
            this.voxels = new Voxel[ChunkConstants.ChunkVolume];
        }
    } 
}