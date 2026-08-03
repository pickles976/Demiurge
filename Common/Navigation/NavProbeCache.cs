namespace Demiurge;

/// <summary>
/// A memo for one burst of navigation probing.
///
/// Standability is the expensive primitive in this codebase: resolving the player capsule against
/// the field costs three sample spheres of 56 trilinear reads each, up to four times, for a single
/// cell. <see cref="VoxelCursor"/> already removes the chunk lookup underneath that, but the
/// arithmetic is the cost, and the only way to avoid arithmetic is to not repeat it.
///
/// It gets repeated constantly. Counting escape routes from one cell asks whether that same cell is
/// standable once per direction and re-probes each cardinal neighbour for every diagonal that
/// borrows it; a cover query then does the whole thing again for the next candidate a few metres
/// away. The answers are identical — terrain cannot change while one query runs — so they are worth
/// keeping.
///
/// Reset between bursts rather than kept alive. The memo is only valid while terrain is unchanged,
/// and "one query" is the longest window this can promise without having to track edits.
///
/// NOT thread safe, deliberately: navigation workers each hold their own, which is also what keeps
/// them from sharing a cursor.
/// </summary>
public sealed class NavProbeCache
{
    internal VoxelCursor Cursor;
    private readonly Dictionary<long, (bool Standable, float SurfaceY)> standable = [];

    /// <summary>
    /// Optional second tier that OUTLIVES the query — see <see cref="NavStandabilityCache"/>. The
    /// per-query memo above still exists in front of it, because a local dictionary hit beats a
    /// concurrent one plus a nine-chunk revision check, and one query re-asks the same handful of
    /// cells many times over.
    /// </summary>
    private readonly NavStandabilityCache? shared;
    private ChunkMap map;

    public NavProbeCache(ChunkMap map, NavStandabilityCache? shared = null)
    {
        Cursor = new VoxelCursor(map);
        this.map = map;
        this.shared = shared;
    }

    /// <summary>Drops every remembered answer and rebinds to the current terrain. Call at the start
    /// of each query, never in the middle of one. The shared tier is not dropped: it invalidates per
    /// chunk revision rather than per query, which is the whole reason it exists.</summary>
    public void Reset(ChunkMap map)
    {
        Cursor = new VoxelCursor(map);
        this.map = map;
        standable.Clear();
    }

    internal bool Lookup(int x, int y, int z, out bool result, out float surfaceY)
    {
        if (standable.TryGetValue(Key(x, y, z), out var cached))
        {
            result = cached.Standable;
            surfaceY = cached.SurfaceY;
            return true;
        }
        if (shared is not null && shared.TryGet(map, x, y, z, out result, out surfaceY))
        {
            standable[Key(x, y, z)] = (result, surfaceY);
            return true;
        }
        result = false;
        surfaceY = 0f;
        return false;
    }

    internal void Store(int x, int y, int z, bool result, float surfaceY)
    {
        standable[Key(x, y, z)] = (result, surfaceY);
        shared?.Store(map, x, y, z, result, surfaceY);
    }

    /// <summary>
    /// Cell coordinates packed into one long. X and Z get 21 bits of signed range each — a million
    /// metres either way, far past any map — and Y gets the 8 the world height needs.
    /// </summary>
    private static long Key(int x, int y, int z)
        => ((long)(x & 0x1FFFFF) << 29)
         | ((long)(z & 0x1FFFFF) << 8)
         | (uint)(y & 0xFF);
}
