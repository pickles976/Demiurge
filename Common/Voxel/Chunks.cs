

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
        /// The world's vertical extent, [WorldMinY, WorldMaxY). ChunkIndex is 2D, so one chunk
        /// spans the full height and no chunk will ever load outside this range.
        /// </summary>
        public const int WorldMinY = 0;
        public const int WorldMaxY = WorldMinY + ChunkHeight;
    }

    /// <summary>A chunk's position on the ground plane. See ChunkTransforms.cs for conventions.</summary>
    public struct ChunkIndex
    {
        public int x;
        public int z;
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
                float distance = y - heightAt;
                chunk.voxels[i].Density = distance;
                chunk.voxels[i].Material = DensityToMaterial(distance);
            }

            return chunk;
        }

        /// <summary>
        /// Labels a voxel from its density. Material is DERIVED from density, never sampled
        /// independently — that one-way dependency is what stops the two fields disagreeing
        /// about whether a voxel exists.
        /// </summary>
        public static BlockType DensityToMaterial(float distance)   // distance = y - height
        {
            if (distance >= 0f) return BlockType.BlockType_Air;     // the invariant, in one place

            float depth = -distance;                                 // how far below the surface
            if (depth < 1f) return BlockType.BlockType_Grass;
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