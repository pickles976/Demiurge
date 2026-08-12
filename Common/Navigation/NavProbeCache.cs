namespace Demiurge;

/// <summary>
/// Per-query standability memo. Reset between queries because terrain may change. Not thread-safe;
/// each navigation worker owns one instance and its cursor.
/// </summary>
public sealed class NavProbeCache
{
    internal VoxelCursor Cursor;
    private readonly Dictionary<long, (bool Standable, float SurfaceY)> standable = [];

    /// <summary>
    /// Optional cross-query tier. The local memo avoids concurrent lookups and revision checks.
    /// </summary>
    private readonly NavStandabilityCache? shared;
    private ChunkMap map;

    public NavProbeCache(ChunkMap map, NavStandabilityCache? shared = null)
    {
        Cursor = new VoxelCursor(map);
        this.map = map;
        this.shared = shared;
    }

    /// <summary>Clears per-query answers and rebinds the cursor. The shared tier uses chunk revisions.</summary>
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
    /// Packs signed 21-bit X/Z and 8-bit Y coordinates.
    /// </summary>
    private static long Key(int x, int y, int z)
        => ((long)(x & 0x1FFFFF) << 29)
         | ((long)(z & 0x1FFFFF) << 8)
         | (uint)(y & 0xFF);
}
