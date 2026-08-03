namespace Demiurge.Tests;

/// <summary>
/// The cache's whole risk is serving an answer the ground no longer supports, so these are
/// invalidation tests before they are anything else. A stale "standable" drops an NPC into a trench
/// somebody just dug, and it would do it rarely enough to look like a physics bug.
/// </summary>
public class NavStandabilityCacheTests
{
    private static bool Probe(ChunkMap map, NavStandabilityCache cache, int x, int z, int y = 12)
    {
        var probes = new NavProbeCache(map, cache);
        return NavTraversal.Standable(probes, x, y, z, out _);
    }

    private static void DigOut(ChunkMap map, int x, int z, int fromY, int toY)
    {
        var index = ChunkTransforms.ChunkAt(x, z);
        var chunk = Assert.IsType<TerrainChunk>(map.Get(index));
        for (int y = fromY; y <= toY; y++)
            chunk[ChunkTransforms.WorldVoxelIndex(x, y, z)] = Voxel.OutsideAbove;
        map.MarkEdited(index, index);
    }

    [Fact]
    public void ASecondProbeOfTheSameCellIsAHit()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavStandabilityCache();

        Probe(map, cache, 0, 0);
        long missesAfterFirst = cache.Misses;
        Probe(map, cache, 0, 0);

        Assert.Equal(missesAfterFirst, cache.Misses);
        Assert.True(cache.Hits > 0);
    }

    [Fact]
    public void TheAnswerSurvivesAcrossSeparateProbeCaches()
    {
        // The point of the second tier: a cover query and an A* expansion standing on the same
        // ground must not each pay 38 us for the same cell.
        var map = SyntheticTerrain.Flat();
        var cache = new NavStandabilityCache();

        Probe(map, cache, 3, 3);
        long hitsBefore = cache.Hits;
        Probe(map, cache, 3, 3);   // a brand new NavProbeCache, same shared tier

        Assert.True(cache.Hits > hitsBefore);
    }

    [Fact]
    public void DiggingTheGroundAwayInvalidatesTheCachedAnswer()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavStandabilityCache();

        Assert.True(Probe(map, cache, 5, 5));

        DigOut(map, 5, 5, fromY: 8, toY: 16);

        Assert.False(Probe(map, cache, 5, 5));
    }

    [Fact]
    public void AnEditInTheNextChunkInvalidatesCellsNearTheBorder()
    {
        // The subtle one. A capsule is 0.8 m wide and every field sample reads a 3x3x3 stencil, so a
        // cell one voxel inside a chunk border depends on the chunk next door. Keying validity on the
        // owning chunk alone would keep serving this answer.
        var map = SyntheticTerrain.Flat();
        var cache = new NavStandabilityCache();

        // x = 15 is the last column of chunk 0; x = 16 is the first of chunk 1.
        Assert.True(Probe(map, cache, 15, 0));
        long revisionSensitiveHits = cache.Hits;

        DigOut(map, 16, 0, fromY: 8, toY: 16);

        // Whatever the new answer is, it must have been RECOMPUTED rather than served from the cache.
        long missesBefore = cache.Misses;
        Probe(map, cache, 15, 0);
        Assert.True(
            cache.Misses > missesBefore,
            "a cell one voxel from the border was served from cache after its neighbour changed");
        Assert.True(cache.Hits >= revisionSensitiveHits);
    }

    [Fact]
    public void AnUnrelatedChunkEditKeepsTheCachedAnswer()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavStandabilityCache();

        Assert.True(Probe(map, cache, 0, 0));
        long hitsBefore = cache.Hits;
        long missesBefore = cache.Misses;

        // CoverBehavior resets its fast per-query tier whenever this global version changes. The
        // shared tier must still recognize that ground around the actor was untouched.
        var farAway = new ChunkIndex { x = 20, z = 20 };
        map.MarkEdited(farAway, farAway);
        Assert.True(Probe(map, cache, 0, 0));

        Assert.True(cache.Hits > hitsBefore);
        Assert.Equal(missesBefore, cache.Misses);
    }

    [Fact]
    public void OverflowDropsTheTableRatherThanServingStaleEntries()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavStandabilityCache();

        for (int i = 0; i < 64; i++)
            Probe(map, cache, i % 16, i / 16);

        Assert.True(cache.Count > 0);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
