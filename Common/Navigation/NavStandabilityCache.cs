using System.Collections.Concurrent;

namespace Demiurge;

/// <summary>
/// Remembers whether a cell is standable, across queries and across systems.
///
/// This is the single largest cost in navigation, and it was being recomputed from scratch every
/// time. Measured: <c>NavTraversal.Standable</c> is <b>38.01 us cold and 0.03 us warm</b> — a 1200x
/// gap — because the cold path resolves the whole player capsule against the signed distance field
/// three or four times over, at roughly 500 voxel reads. <c>NavProbeCache</c> already captured that
/// gap and then threw it away at the end of each query, so a cover query, an A* expansion and a flow
/// field solve standing on the same ground each paid full price for the same answer.
///
/// It is worth caching because standability is a PURE FUNCTION OF THE FIELD: same voxels, same
/// answer, no actor state involved. That is also what makes invalidation tractable — the answer
/// survives exactly as long as the terrain under it does.
///
/// <para>
/// <b>Invalidation covers the 3x3 chunk neighbourhood, not the owning chunk.</b> A capsule is 0.8 m
/// wide and each field sample reads a 3x3x3 gradient stencil around itself, so a cell near a chunk
/// border genuinely depends on voxels in the chunk next door. Keying on the owning chunk alone would
/// serve stale answers along every border — rare enough to survive testing and wrong enough to drop
/// an NPC through a freshly dug trench wall.
/// </para>
/// <para>
/// Bounded, because a session's worth of unique cells is not. On overflow the whole table is dropped
/// rather than evicted one by one: entries are equally valuable, the cache refills in seconds of
/// play, and an LRU's bookkeeping would cost a meaningful fraction of the 0.03 us hit it protects.
/// </para>
/// <para>
/// <b>Only worth attaching to workloads that RE-ASK.</b> Wiring it into the flow field solve measured
/// 88.9 s -> 116.7 s at a 17% hit rate: a Dijkstra sweep settles each cell once, so there is nothing
/// to reuse, and every miss still pays the concurrent lookup plus <see cref="NeighbourhoodRevision"/>'s
/// nine chunk probes — about eighteen dictionary operations of pure overhead per miss. The intended
/// consumers are cover queries (thirteen candidates plus escape routes, re-run by squadmates on
/// overlapping ground ~25 times a second) and successive A* searches from nearby cells. Measure the
/// hit rate before attaching it to anything new; below roughly 50% it is a regression.
/// </para>
/// <para>
/// The revision check is also the first thing to make cheaper if that matters — caching each chunk's
/// neighbourhood revision and refreshing it only when <c>ChunkMap.EditVersion</c> moves would turn
/// nine probes into one comparison.
/// </para>
/// </summary>
public sealed class NavStandabilityCache
{
    /// <summary>
    /// Entries before the table is dropped. About 24 MB at ~40 bytes an entry — a few minutes of
    /// heavy navigation, and comfortably more than the 130k cells four coarse flow fields touch.
    /// </summary>
    public const int MaximumEntries = 600_000;

    private readonly record struct Entry(bool Standable, float SurfaceY, long Revision);

    private readonly ConcurrentDictionary<long, Entry> entries = new();

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public int Count => entries.Count;

    /// <summary>
    /// The cached answer for a cell, if one is still valid for the current terrain.
    /// </summary>
    public bool TryGet(ChunkMap map, int x, int y, int z, out bool standable, out float surfaceY)
    {
        standable = false;
        surfaceY = 0f;
        var cell = new NavCell(x, y, z);
        if (!entries.TryGetValue(cell.Key, out var entry)
            || entry.Revision != NeighbourhoodRevision(map, x, z))
        {
            Misses++;
            return false;
        }

        Hits++;
        standable = entry.Standable;
        surfaceY = entry.SurfaceY;
        return true;
    }

    public void Store(ChunkMap map, int x, int y, int z, bool standable, float surfaceY)
    {
        if (entries.Count >= MaximumEntries) entries.Clear();
        entries[new NavCell(x, y, z).Key] =
            new Entry(standable, surfaceY, NeighbourhoodRevision(map, x, z));
    }

    public void Clear() => entries.Clear();

    /// <summary>
    /// A revision that changes whenever any chunk this cell's answer could depend on is edited.
    ///
    /// Summed rather than maxed: two edits in different neighbours can leave the maximum unchanged
    /// while both matter, and edit versions are monotonic so a sum is monotonic too. Nine cheap
    /// dictionary lookups against a 38 us recomputation is a trade worth making every time.
    /// </summary>
    private static long NeighbourhoodRevision(ChunkMap map, int x, int z)
    {
        var centre = ChunkTransforms.ChunkAt(x, z);
        long revision = 0;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                revision += map.ChunkEditVersion(
                    new ChunkIndex { x = centre.x + dx, z = centre.z + dz });
        return revision;
    }
}
