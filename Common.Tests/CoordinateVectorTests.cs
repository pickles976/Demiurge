using System.Numerics;
using Demiurge;

namespace DemiurgeCommon.Tests;

/// <summary>
/// The example-based half of the coordinate suite: every case here is ported verbatim from the
/// Rust reference at /home/sebas/Projects/Demiurge (src/chunks/utils.rs, mod tests). That project
/// renders this terrain correctly, so its expectations are the spec — if a change makes one of
/// these fail, the change is wrong, not the test.
/// </summary>
public class CoordinateVectorTests
{
    // --- convert_local_block_coords_to_block_index ---

    [Theory]
    [InlineData(0, 0, 0)]        // first
    [InlineData(15, 15, 255)]    // last
    [InlineData(3, 5, 83)]       // order: y is the major axis
    public void LocalBlockCoordsToIndex(ushort x, ushort y, int expected)
    {
        var coords = new BlockCoords { x = x, y = y };
        Assert.Equal(expected, ChunkTransforms.ConvertLocalBlockCoordsToBlockIndex(coords));
    }

    // --- convert_vec3_to_chunk_coordinates ---

    [Theory]
    [InlineData(0f, 0f, 0f, 0, 0)]
    [InlineData(16f, 0f, 15f, 1, 0)]
    [InlineData(16f, 0f, 22f, 1, 1)]
    [InlineData(-1f, 0f, 1f, -1, 0)]
    [InlineData(0f, 0f, -1f, 0, -1)]
    [InlineData(-1f, 0f, -1f, -1, -1)]
    // Exact negative boundaries: world -16 is the FIRST column of chunk -1, not the last of -2.
    // The Rust gets these wrong and never tests them; we use real floor division.
    [InlineData(-16f, 0f, 0f, -1, 0)]
    [InlineData(0f, 0f, -16f, 0, -1)]
    public void Vector3ToChunkCoordinates(float px, float py, float pz, int expectedX, int expectedY)
    {
        var index = ChunkTransforms.ConvertVector3ToChunkCoordinates(new Vector3(px, py, pz));

        Assert.Equal(expectedX, index.x);
        Assert.Equal(expectedY, index.y);
    }

    /// <summary>
    /// Y is height and must not participate. This is the exact bug that shipped: the function read
    /// position.Y where it meant position.Z, which only shows up once you leave the origin chunk.
    /// </summary>
    [Fact]
    public void Vector3ToChunkCoordinatesIgnoresHeight()
    {
        var atGround = ChunkTransforms.ConvertVector3ToChunkCoordinates(new Vector3(20f, 0f, 40f));
        var highUp = ChunkTransforms.ConvertVector3ToChunkCoordinates(new Vector3(20f, 500f, 40f));
        var buried = ChunkTransforms.ConvertVector3ToChunkCoordinates(new Vector3(20f, -500f, 40f));

        Assert.Equal(atGround, highUp);
        Assert.Equal(atGround, buried);
    }

    // --- convert_vec3_to_local_block_coordinates ---

    [Theory]
    [InlineData(0f, 0f, 0f, 0, 0)]
    [InlineData(17.1f, 0f, 0f, 1, 0)]
    [InlineData(-1f, 0f, -16.1f, 15, 15)]
    public void Vector3ToLocalBlockCoordinates(float px, float py, float pz, int expectedX, int expectedY)
    {
        var coords = ChunkTransforms.ConvertVector3ToLocalBlockCoordinates(new Vector3(px, py, pz));

        Assert.Equal(expectedX, coords.x);
        Assert.Equal(expectedY, coords.y);
    }

    // --- convert_chunk_coords_and_block_index_to_global_block_coordinates ---

    [Theory]
    [InlineData(0, 0, 0, 0f, 0f)]
    [InlineData(-1, -2, 0, -16f, -32f)]
    [InlineData(0, 0, 13, 13f, 0f)]     // still in the first row
    [InlineData(0, 0, 87, 7f, 5f)]      // 87 = 5*16 + 7
    public void ChunkAndBlockIndexToGlobalCoordinates(int chunkX, int chunkY, int blockIndex, float expectedX, float expectedY)
    {
        var index = new ChunkIndex { x = chunkX, y = chunkY };
        var coords = ChunkTransforms.ConvertChunkCoordinatesAndBlockIndexToGlobalBlockCoordinates(index, blockIndex);

        Assert.Equal(expectedX, coords.X);
        Assert.Equal(expectedY, coords.Y);
    }
}
