namespace Demiurge.Tests;

public sealed class ChunkCloneTests
{
    [Fact]
    public void DeepClonePreservesCompactStorageAndIsolatesMutations()
    {
        var index = new ChunkIndex { x = 2, z = -3 };
        var chunk = new TerrainChunk(index);
        var stone = new Voxel { Distance = -1f, Material = BlockType.BlockType_Stone };
        var grass = new Voxel { Distance = -0.25f, Material = BlockType.BlockType_Grass };
        chunk.FillSlab(0, stone);
        chunk[ChunkConstants.ChunkSize * 3 + 7] = grass;
        var map = new ChunkMap();
        map.Insert(chunk);

        var clone = map.DeepClone();
        var clonedChunk = Assert.IsType<TerrainChunk>(clone.Get(index));

        Assert.Equal(chunk.AllocatedSlabs, clonedChunk.AllocatedSlabs);
        Assert.Equal(stone, clonedChunk[0]);
        Assert.Equal(grass, clonedChunk[ChunkConstants.ChunkSize * 3 + 7]);

        chunk[0] = default;
        clonedChunk[ChunkConstants.ChunkSize * 3 + 7] = stone;

        Assert.Equal(stone, clonedChunk[0]);
        Assert.Equal(grass, chunk[ChunkConstants.ChunkSize * 3 + 7]);
    }
}
