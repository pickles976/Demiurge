namespace Demiurge.Tests;

public sealed class NavTraversalCacheTests
{
    private static readonly (int X, int Z, int AroundY) Key = (0, 0, 12);
    private static readonly NavTraversalCache.StandableResult Answer =
        new(true, new NavCell(0, 12, 0));

    [Fact]
    public void UnrelatedChunkEditKeepsTraversalAnswer()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavTraversalCache();
        long generation = NavTraversalCache.Begin(map);
        cache.StoreStandable(map, generation, Key, Answer);

        var farAway = new ChunkIndex { x = 20, z = 20 };
        map.MarkEdited(farAway, farAway);

        Assert.True(cache.TryGetStandable(map, Key, out var result));
        Assert.Equal(Answer, result);

        // The hit is promoted to the new global generation. Promotion must not make a later local
        // edit invisible.
        var owningChunk = new ChunkIndex { x = 0, z = 0 };
        map.MarkEdited(owningChunk, owningChunk);
        Assert.False(cache.TryGetStandable(map, Key, out _));
    }

    [Fact]
    public void NeighbouringChunkEditInvalidatesTraversalAnswer()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavTraversalCache();
        long generation = NavTraversalCache.Begin(map);
        cache.StoreStandable(map, generation, Key, Answer);

        var neighbour = new ChunkIndex { x = 1, z = 0 };
        map.MarkEdited(neighbour, neighbour);

        Assert.False(cache.TryGetStandable(map, Key, out _));
    }

    [Fact]
    public void FullMapResetInvalidatesTraversalAnswer()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavTraversalCache();
        long generation = NavTraversalCache.Begin(map);
        cache.StoreStandable(map, generation, Key, Answer);

        map.Reset();

        Assert.False(cache.TryGetStandable(map, Key, out _));
    }

    [Fact]
    public void AnswerIsNotPublishedIfItsNeighbourhoodChangedDuringTheFill()
    {
        var map = SyntheticTerrain.Flat();
        var cache = new NavTraversalCache();
        long generation = NavTraversalCache.Begin(map);

        var owningChunk = new ChunkIndex { x = 0, z = 0 };
        map.MarkEdited(owningChunk, owningChunk);
        cache.StoreStandable(map, generation, Key, Answer);

        Assert.False(cache.TryGetStandable(map, Key, out _));
    }
}
