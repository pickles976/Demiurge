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
    // The Rust's 2D (x, y) is our (x, z) at height 0, which is why the voxel formula's y = 0 slice
    // has to equal the column formula.

    [Theory]
    [InlineData(0, 0, 0)]        // first
    [InlineData(15, 15, 255)]    // last
    [InlineData(3, 5, 83)]       // order: z is the major axis
    public void LocalBlockCoordsToIndex(int x, int z, int expected)
    {
        Assert.Equal(expected, ChunkTransforms.LocalVoxelIndex(x, 0, z));
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
    public void Vector3ToChunkCoordinates(float px, float py, float pz, int expectedX, int expectedZ)
    {
        var index = ChunkTransforms.ChunkAt(new Vector3(px, py, pz));

        Assert.Equal(expectedX, index.x);
        Assert.Equal(expectedZ, index.z);
    }

    /// <summary>
    /// Y is height and must not participate. This is the exact bug that shipped: the function read
    /// position.Y where it meant position.Z, which only shows up once you leave the origin chunk.
    /// </summary>
    [Fact]
    public void Vector3ToChunkCoordinatesIgnoresHeight()
    {
        var atGround = ChunkTransforms.ChunkAt(new Vector3(20f, 0f, 40f));
        var highUp = ChunkTransforms.ChunkAt(new Vector3(20f, 500f, 40f));
        var buried = ChunkTransforms.ChunkAt(new Vector3(20f, -500f, 40f));

        Assert.Equal(atGround, highUp);
        Assert.Equal(atGround, buried);
    }

    // --- convert_vec3_to_local_block_coordinates ---
    // Same vectors, now against the integer path, round-tripped through the flat index. The float
    // helper this used to call is gone: flooring a position and calling the integer path is the
    // only derivation of a local coordinate, so there's nowhere for two of them to disagree.

    [Theory]
    [InlineData(0f, 0f, 0, 0)]
    [InlineData(17.1f, 0f, 1, 0)]
    [InlineData(-1f, -16.1f, 15, 15)]
    public void WorldPositionToLocalCoordinates(float px, float pz, int expectedX, int expectedZ)
    {
        int index = ChunkTransforms.WorldVoxelIndex(
            (int)MathF.Floor(px), ChunkConstants.WorldMinY, (int)MathF.Floor(pz));

        var local = ChunkTransforms.LocalVoxelCoords(index);

        Assert.Equal(expectedX, local.x);
        Assert.Equal(expectedZ, local.z);
    }

    // --- convert_chunk_coords_and_block_index_to_global_block_coordinates ---

    [Theory]
    [InlineData(0, 0, 0, 0f, 0f)]
    [InlineData(-1, -2, 0, -16f, -32f)]
    [InlineData(0, 0, 13, 13f, 0f)]     // still in the first row
    [InlineData(0, 0, 87, 7f, 5f)]      // 87 = 5*16 + 7
    public void ChunkAndBlockIndexToGlobalCoordinates(int chunkX, int chunkZ, int blockIndex, float expectedX, float expectedZ)
    {
        var index = new ChunkIndex { x = chunkX, z = chunkZ };
        var coords = ChunkTransforms.ColumnWorldPosition(index, blockIndex);

        Assert.Equal(expectedX, coords.X);
        Assert.Equal(expectedZ, coords.Y);
    }
}
