

namespace Demiurge
{


    public static class ChunkConstants {
        public const int ChunkWidth = 16;
        public const int ChunkHeight = 128;
        public const int ChunkSize = ChunkWidth * ChunkWidth;
        public const int ChunkVolume = ChunkWidth * ChunkWidth * ChunkHeight;

        /// <summary>Edge length of one block in world units.</summary>
        public const float TileSize = 1.0f;
    }

    public struct ChunkIndex
    {
        public int x;
        public int y;
    }

    /// <summary>
    /// A COLUMN's position inside one chunk, on the ground plane only: both fields are
    /// 0..ChunkWidth-1, and <c>y</c> is the second GROUND axis (world Z), not height.
    /// Addresses the 256-entry heightmap. For a voxel use <see cref="LocalVoxelCoords"/>.
    /// </summary>
    public struct BlockCoords
    {
        public UInt16 x;
        public UInt16 y;
    }

    /// <summary>
    /// A VOXEL's position inside one chunk: x and z are 0..ChunkWidth-1, y is 0..ChunkHeight-1
    /// and means HEIGHT. Note the disagreement with <see cref="BlockCoords"/> and
    /// <see cref="ChunkIndex"/>, whose <c>y</c> fields are world Z — see README.md.
    /// </summary>
    public struct LocalVoxelCoords
    {
        public int x;
        public int y;   // height
        public int z;
    }

    public struct ChunkMap
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
        
        public TerrainChunk? Get(ChunkIndex index)
        {
            TerrainChunk chunk;
            chunks.TryGetValue(index, out chunk);
            return chunk;
        }

        public void Insert(TerrainChunk chunk)
        {
            chunks[chunk.index] = chunk;
        }

    }

    // Not stored anywhere, just an intermediate type so we have a contract for the output of some computations
    public struct ChunkCorners
    {
        public float xPlus;
        public float xMinus;

        public float zPlus;
        public float zMinus;
    }

    public class ChunkGenerator
    {

        const float Amplitude = 4f;
        const float SeaLevel  = 8f;

        public static TerrainChunk GenerateChunk(ChunkIndex index)
        {

            float[] heightMap = NoiseGen.GenerateNoiseForChunk(index);
            TerrainChunk chunk = new TerrainChunk(index);

            // Get the density of each voxel from the heightmap
            for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            {
                // Height at X,Z voxel index
                float heightAt = heightMap[ChunkTransforms.GetColumnIndexFromVoxelIndex(i)] * Amplitude + SeaLevel;  // i % 256
                int y = ChunkTransforms.GetLocalYFromVoxelIndex(i);                           // i / 256
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

    public struct TerrainChunk
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