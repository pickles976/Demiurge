namespace Demiurge.Tests;

/// <summary>
/// <see cref="VoxelCursor"/> is a cache in front of <see cref="ChunkMap.TryGetVoxel"/> and must be
/// indistinguishable from it. That equivalence is the whole safety argument for leaving the
/// collision maths above it untouched, so it is pinned here rather than assumed: sweep the
/// coordinates that have any chance of behaving differently — chunk borders, negative coordinates,
/// the world floor and ceiling, missing chunks — and compare both results voxel for voxel.
/// </summary>
public class VoxelCursorTests
{
    private static ChunkMap World()
    {
        // Real generated terrain rather than a synthetic field, so the voxels differ from each other
        // and a mixed-up index cannot pass by returning the same value everywhere.
        var map = new ChunkMap();
        WorldGen.Generate(map);
        return map;
    }

    [Fact]
    public void MatchesTryGetVoxelAcrossChunkBordersAndTheWorldFloor()
    {
        var map = World();
        var cursor = new VoxelCursor(map);
        int compared = 0;

        // Two chunks' worth in each horizontal direction, so every read crosses borders in both
        // axes, plus the full height including the out-of-range sentinels at either end.
        for (int x = -ChunkConstants.ChunkWidth - 3; x <= ChunkConstants.ChunkWidth + 3; x++)
            for (int z = -ChunkConstants.ChunkWidth - 3; z <= ChunkConstants.ChunkWidth + 3; z += 3)
                for (int y = ChunkConstants.WorldMinY - 2; y <= ChunkConstants.WorldMaxY + 1; y += 7)
                {
                    bool expected = map.TryGetVoxel(x, y, z, out var expectedVoxel);
                    bool actual = cursor.TryGet(x, y, z, out var actualVoxel);

                    Assert.Equal(expected, actual);
                    Assert.Equal(expectedVoxel.Distance, actualVoxel.Distance);
                    Assert.Equal(expectedVoxel.Material, actualVoxel.Material);
                    compared++;
                }

        // Guards against the sweep silently collapsing to nothing; the coverage that matters is the
        // borders and sentinels above, not the raw count.
        Assert.True(compared > 9_000, $"only compared {compared} voxels");
    }

    [Fact]
    public void ReportsMissingChunksAsMissingHoweverOftenItIsAsked()
    {
        // One chunk only: everything outside it has no data, and "no data" must never soften into
        // air just because the cursor remembered something.
        var map = new ChunkMap();
        map.Insert(new TerrainChunk(new ChunkIndex { x = 0, z = 0 }));
        var cursor = new VoxelCursor(map);

        for (int repeat = 0; repeat < 3; repeat++)
            for (int x = -ChunkConstants.ChunkWidth; x < 2 * ChunkConstants.ChunkWidth; x++)
                for (int z = -ChunkConstants.ChunkWidth; z < 2 * ChunkConstants.ChunkWidth; z += 5)
                {
                    bool expected = map.TryGetVoxel(x, 20, z, out var expectedVoxel);
                    bool actual = cursor.TryGet(x, 20, z, out var actualVoxel);

                    Assert.Equal(expected, actual);
                    Assert.Equal(expectedVoxel.Distance, actualVoxel.Distance);
                }
    }

    [Fact]
    public void SeesEditsMadeAfterTheChunkWasRemembered()
    {
        // The cursor caches the chunk REFERENCE, not its contents, so an edit through the map is
        // visible immediately. A cursor that snapshotted voxels would let an actor walk through a
        // wall that had just been dug.
        var map = new ChunkMap();
        var chunk = new TerrainChunk(new ChunkIndex { x = 0, z = 0 });
        map.Insert(chunk);
        var cursor = new VoxelCursor(map);

        Assert.True(cursor.TryGet(4, 20, 4, out var before));

        int index = ChunkTransforms.WorldVoxelIndex(4, 20, 4);
        chunk[index] = new Voxel { Distance = before.Distance - 1f };

        Assert.True(cursor.TryGet(4, 20, 4, out var after));
        Assert.Equal(chunk[index].Distance, after.Distance);
        Assert.NotEqual(before.Distance, after.Distance);
    }
}
