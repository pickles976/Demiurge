using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// The terrain chunks whose current contents make a selected fighting position valid.
///
/// A cover choice depends on local capsule/escape geometry and on the terrain sampled by its
/// sightlines. It does not depend on a shovel bite on the other side of the map, nor do we need to
/// discard valid cover merely because that bite might have made a different position more
/// attractive. The ordinary tactical reconsideration can discover alternatives later.
/// </summary>
internal static class CoverTerrainDependency
{
    // The selected cell's capsule checks, eight escape neighbours, diagonal cardinal checks and
    // possible corner-peek cells all fit inside this footprint. Converting the footprint itself to
    // chunks, rather than taking an unconditional 3x3 chunk apron, matters: in the middle of a
    // 16-metre chunk this records one local chunk, not nine.
    private const float LocalGeometryRadius = 4f;

    // A ray march normally samples the eight corners of its containing voxel. Its final surface
    // normal also samples half a voxel to either side. One metre is therefore a conservative
    // horizontal footprint around the geometric segment without widening it by a whole chunk.
    private const float RaySampleApron = 1f;

    public static ChunkIndex[] Capture(
        Vector3 cover,
        Vector3 peek,
        ReadOnlySpan<Vector3> threats)
    {
        var chunks = new HashSet<ChunkIndex>();
        AddBounds(
            chunks,
            cover.X - LocalGeometryRadius,
            cover.Z - LocalGeometryRadius,
            cover.X + LocalGeometryRadius,
            cover.Z + LocalGeometryRadius);
        AddBounds(
            chunks,
            peek.X - LocalGeometryRadius,
            peek.Z - LocalGeometryRadius,
            peek.X + LocalGeometryRadius,
            peek.Z + LocalGeometryRadius);

        foreach (Vector3 threat in threats)
        {
            AddRay(chunks, cover, threat);
            AddRay(chunks, peek, threat);
        }

        var result = new ChunkIndex[chunks.Count];
        chunks.CopyTo(result);
        Array.Sort(result, static (a, b) =>
        {
            int x = a.x.CompareTo(b.x);
            return x != 0 ? x : a.z.CompareTo(b.z);
        });
        return result;
    }

    public static ChunkIndex[] Capture(Vector3 cover, Vector3 peek, Vector3 threat)
    {
        Span<Vector3> threats = stackalloc Vector3[1];
        threats[0] = threat;
        return Capture(cover, peek, threats);
    }

    /// <summary>
    /// Whether terrain relevant to a choice made at <paramref name="generation"/> has changed.
    /// An empty set is treated conservatively for cover destinations created by code that has not
    /// supplied spatial dependencies.
    /// </summary>
    public static bool ChangedSince(
        ChunkMap map,
        IReadOnlyList<ChunkIndex> chunks,
        long generation)
    {
        if (map.EditVersion == generation) return false;
        if (map.GlobalInvalidationVersion > generation || chunks.Count == 0) return true;

        for (int i = 0; i < chunks.Count; i++)
            if (map.ChunkEditVersion(chunks[i]) > generation)
                return true;
        return false;
    }

    private static void AddBounds(
        HashSet<ChunkIndex> chunks,
        float minX,
        float minZ,
        float maxX,
        float maxZ)
    {
        var first = ChunkTransforms.ChunkAt(
            (int)MathF.Floor(minX),
            (int)MathF.Floor(minZ));
        var last = ChunkTransforms.ChunkAt(
            (int)MathF.Floor(maxX),
            (int)MathF.Floor(maxZ));
        for (int z = first.z; z <= last.z; z++)
            for (int x = first.x; x <= last.x; x++)
                chunks.Add(new ChunkIndex { x = x, z = z });
    }

    /// <summary>Adds only chunks whose voxel area lies within the ray sampler's footprint.</summary>
    private static void AddRay(HashSet<ChunkIndex> chunks, Vector3 from, Vector3 to)
    {
        float minX = MathF.Min(from.X, to.X) - RaySampleApron;
        float minZ = MathF.Min(from.Z, to.Z) - RaySampleApron;
        float maxX = MathF.Max(from.X, to.X) + RaySampleApron;
        float maxZ = MathF.Max(from.Z, to.Z) + RaySampleApron;
        var first = ChunkTransforms.ChunkAt(
            (int)MathF.Floor(minX),
            (int)MathF.Floor(minZ));
        var last = ChunkTransforms.ChunkAt(
            (int)MathF.Floor(maxX),
            (int)MathF.Floor(maxZ));

        for (int z = first.z; z <= last.z; z++)
            for (int x = first.x; x <= last.x; x++)
            {
                float chunkMinX = x * ChunkConstants.ChunkWidth - RaySampleApron;
                float chunkMinZ = z * ChunkConstants.ChunkWidth - RaySampleApron;
                float chunkMaxX = (x + 1) * ChunkConstants.ChunkWidth + RaySampleApron;
                float chunkMaxZ = (z + 1) * ChunkConstants.ChunkWidth + RaySampleApron;
                if (SegmentIntersectsRectangle(
                        from.X,
                        from.Z,
                        to.X,
                        to.Z,
                        chunkMinX,
                        chunkMinZ,
                        chunkMaxX,
                        chunkMaxZ))
                    chunks.Add(new ChunkIndex { x = x, z = z });
            }
    }

    private static bool SegmentIntersectsRectangle(
        float fromX,
        float fromZ,
        float toX,
        float toZ,
        float minX,
        float minZ,
        float maxX,
        float maxZ)
    {
        float enter = 0f;
        float leave = 1f;
        return ClipAxis(fromX, toX - fromX, minX, maxX, ref enter, ref leave)
            && ClipAxis(fromZ, toZ - fromZ, minZ, maxZ, ref enter, ref leave);
    }

    private static bool ClipAxis(
        float origin,
        float delta,
        float minimum,
        float maximum,
        ref float enter,
        ref float leave)
    {
        if (MathF.Abs(delta) < 1e-6f)
            return origin >= minimum && origin <= maximum;

        float first = (minimum - origin) / delta;
        float last = (maximum - origin) / delta;
        if (first > last) (first, last) = (last, first);
        enter = MathF.Max(enter, first);
        leave = MathF.Min(leave, last);
        return enter <= leave;
    }
}
