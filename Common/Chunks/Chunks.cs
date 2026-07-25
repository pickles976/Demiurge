

namespace Demiurge
{


    public static class ChunkConstants {
        public const int ChunkWidth = 16;
        public const int ChunkSize = ChunkWidth * ChunkWidth;
    }

    public struct ChunkIndex
    {
        public int x;
        public int y;
    }

    public struct BlockCoords
    {
        public UInt16 x;
        public UInt16 y;
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

    public struct TerrainChunk
    {
        public ChunkIndex index;

        public float[] tiles;

        public TerrainChunk(ChunkIndex index)
        {
            this.index = index;
            this.tiles = new float[ChunkConstants.ChunkSize];
        }
    } 
}