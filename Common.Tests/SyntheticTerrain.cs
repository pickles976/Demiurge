namespace Demiurge.Tests;

/// <summary>
/// Hand-built voxel fields for collision tests. Every case is an exact distance function evaluated
/// at grid points, so expected positions are arithmetic rather than "looks about right" — which is
/// the whole reason these tests are worth having over looking at the game.
///
/// Written through the same world-floor clamp real generation uses, so the bedrock plane behaves
/// here exactly as it does in the shipped world.
/// </summary>
public static class SyntheticTerrain
{
    /// <summary>Surface height used by the flat cases. Off a whole number so nothing lands on a grid point by luck.</summary>
    public const float GroundHeight = 12.5f;

    /// <summary>
    /// A map of (2 * chunkRadius + 1)^2 chunks around the origin, filled from a signed distance
    /// function of WORLD voxel coordinates. Negative is solid, matching every other consumer.
    /// </summary>
    public static ChunkMap Build(Func<int, int, int, float> distance, int chunkRadius = 1)
    {
        var map = new ChunkMap();

        for (int cz = -chunkRadius; cz <= chunkRadius; cz++)
            for (int cx = -chunkRadius; cx <= chunkRadius; cx++)
                map.Insert(Chunk(new ChunkIndex { x = cx, z = cz }, distance));

        return map;
    }

    /// <summary>A single chunk, for the "walked off the edge of the loaded world" cases.</summary>
    public static ChunkMap BuildOne(ChunkIndex index, Func<int, int, int, float> distance)
    {
        var map = new ChunkMap();
        map.Insert(Chunk(index, distance));
        return map;
    }

    static TerrainChunk Chunk(ChunkIndex index, Func<int, int, int, float> distance)
    {
        var chunk = new TerrainChunk(index);
        (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            (int lx, int ly, int lz) = ChunkTransforms.LocalVoxelCoords(i);
            int worldY = ChunkConstants.WorldMinY + ly;

            float d = ChunkConstants.ClampToWorldFloor(worldY, distance(originX + lx, worldY, originZ + lz));

            chunk.voxels[i].Distance = d;
            chunk.voxels[i].Material = ChunkGenerator.DensityToMaterial(chunk.voxels[i].Distance, d);
        }

        return chunk;
    }

    // ---- The fields themselves ----

    /// <summary>Flat ground: solid below <see cref="GroundHeight"/>.</summary>
    public static ChunkMap Flat() => Build((x, y, z) => y - GroundHeight);

    /// <summary>
    /// Ground rising along +X at the given angle. The surface passes through GroundHeight at x = 0.
    /// </summary>
    public static ChunkMap Slope(float degrees)
    {
        float rise = MathF.Tan(degrees * (MathF.PI / 180f));
        return Build((x, y, z) => y - GroundHeight - rise * x);
    }

    /// <summary>Flat ground plus a wall filling x &gt;= wallX. Union of two fields, hence min.</summary>
    public static ChunkMap Wall(float wallX)
        => Build((x, y, z) => MathF.Min(y - GroundHeight, wallX - x));

    /// <summary>Flat ground under a ceiling filling y &gt;= ceilingY.</summary>
    public static ChunkMap Ceiling(float ceilingY)
        => Build((x, y, z) => MathF.Min(y - GroundHeight, ceilingY - y));

    /// <summary>
    /// A floating slab solid only in [lowY, highY] — open air above AND below. Nothing about this is
    /// expressible as a heightmap, which is the point: it is the overhang/cave case.
    /// </summary>
    public static ChunkMap Slab(float lowY, float highY)
        => Build((x, y, z) => MathF.Max(lowY - y, y - highY));

    /// <summary>Solid everywhere, well past the quantization clamp. The buried case.</summary>
    public static ChunkMap Solid() => Build((x, y, z) => -8f);
}
