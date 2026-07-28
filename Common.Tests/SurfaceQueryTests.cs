namespace Demiurge.Tests;

/// <summary>
/// Where the ground is. The server places items and (later) players with this, so an off-by-one puts
/// everything half a voxel into the terrain or floating above it — and both look like art bugs.
/// </summary>
public class SurfaceQueryTests
{
    /// <summary>One chunk whose density is a flat surface at the given world height.</summary>
    static ChunkMap FlatWorld(float surfaceY)
    {
        var map = new ChunkMap();
        var chunk = new TerrainChunk(new ChunkIndex { x = 0, z = 0 });

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            float d = ChunkTransforms.LocalYOf(i) + ChunkConstants.WorldMinY - surfaceY;
            var vi = new Voxel { Distance = d };
            vi.Material = ChunkGenerator.DensityToMaterial(vi.Distance, d);
            chunk[i] = vi;
        }

        map.Insert(chunk);
        return map;
    }

    /// <summary>
    /// The whole point of a density field over block types: the surface sits where the noise said,
    /// not on a voxel boundary.
    /// </summary>
    [Theory]
    [InlineData(12.5f)]
    [InlineData(12.0f)]
    [InlineData(8.24f)]
    public void SurfaceIsFoundAtSubVoxelPrecision(float surfaceY)
    {
        float? y = SurfaceQuery.HighestSurfaceY(FlatWorld(surfaceY), 8, 8);

        Assert.NotNull(y);

        // Tolerance is one quantization step (0.02) plus slack — the stored field can't represent
        // the exact height, and that limit is the reason for the tolerance rather than sloppiness.
        Assert.Equal(surfaceY, y!.Value, 1);
    }

    /// <summary>A column outside any loaded chunk has no answer, and must not invent one.</summary>
    [Fact]
    public void UnloadedColumnHasNoSurface()
    {
        Assert.Null(SurfaceQuery.HighestSurfaceY(FlatWorld(12.5f), 9999, 9999));
    }

    [Fact]
    public void HighestSurfaceReportsTheMaterialUnderTheSurface()
    {
        var map = FlatWorld(12.5f);

        var surface = SurfaceQuery.HighestSurface(map, 8, 8);

        Assert.NotNull(surface);
        Assert.Equal(BlockType.BlockType_Grass, surface!.Value.Material);
        Assert.Equal(12.5f, surface.Value.Y, 1);
    }

    [Fact]
    public void TreeEligibilityRequiresGrassSurface()
    {
        var map = FlatWorld(12.5f);

        Assert.True(TreePlacement.IsTreeEligible(map, 8, 8, out _));

        var chunk = map.Get(new ChunkIndex { x = 0, z = 0 })!;
        for (int y = ChunkConstants.WorldMinY; y < ChunkConstants.WorldMaxY; y++)
        {
            int i = ChunkTransforms.WorldVoxelIndex(8, y, 8);
            var voxel = chunk[i];
            if (voxel.Distance < 0f) voxel.Material = BlockType.BlockType_Stone;
            chunk[i] = voxel;
        }

        Assert.False(TreePlacement.IsTreeEligible(map, 8, 8, out _));
    }

    /// <summary>
    /// Carving through a column must not make the surface vanish — the bedrock plane is always solid,
    /// so there is always something to stand on. See ChunkConstants.BedrockThickness.
    /// </summary>
    [Fact]
    public void CarvedColumnFallsBackToBedrock()
    {
        var map = FlatWorld(12.5f);
        TerrainEdits.CarveTrench(map.Get(new ChunkIndex { x = 0, z = 0 })!);

        float? y = SurfaceQuery.HighestSurfaceY(map, 8, 8);

        Assert.NotNull(y);
        Assert.InRange(y!.Value, ChunkConstants.WorldMinY, ChunkConstants.WorldMinY + 2f);
    }

    /// <summary>Spawning keeps the requested column and only solves for height.</summary>
    [Fact]
    public void SurfacePositionKeepsXAndZ()
    {
        var position = SurfaceQuery.SurfacePosition(FlatWorld(12.5f), 3.25f, 7.75f);

        Assert.Equal(3.25f, position.X, 4);
        Assert.Equal(7.75f, position.Z, 4);
        Assert.Equal(12.5f, position.Y, 1);
    }
}
