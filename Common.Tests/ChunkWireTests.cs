namespace Demiurge.Tests;

/// <summary>
/// Chunk serialization. Worth pinning because a bug here corrupts terrain silently — the client
/// renders something plausible that the server disagrees with, which is the hardest class of bug to
/// see. Pure byte-in/byte-out, no engine.
/// </summary>
public class ChunkWireTests
{
    /// <summary>A real generated chunk: uniform slabs above and below, mixed ones at the surface.</summary>
    static TerrainChunk Realistic() => ChunkGenerator.GenerateChunk(new ChunkIndex { x = 0, z = 0 });

    /// <summary>A generated chunk with the debug wall and trench cut into it, so edited voxels are covered.</summary>
    static TerrainChunk Edited()
    {
        var chunk = ChunkGenerator.GenerateChunk(new ChunkIndex { x = 0, z = 0 });
        TerrainEdits.AddWall(chunk);
        TerrainEdits.CarveTrench(chunk);
        return chunk;
    }

    /// <summary>
    /// Deliberately hostile to every compression path at once: densities that contradict what the
    /// material plane implies (so the saturation mask cannot omit them), and a palette width that
    /// varies per slab from 1 value up past the 16 a palette can hold. Each material encoding —
    /// uniform, palette-RLE, packed indices, raw — and both density encodings get exercised somewhere
    /// in the column.
    /// </summary>
    static TerrainChunk Adversarial()
    {
        var chunk = new TerrainChunk(new ChunkIndex { x = 4, z = -7 });

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            int slab = i / ChunkConstants.ChunkSize;
            int within = i % ChunkConstants.ChunkSize;

            var v = chunk[i];
            v.Density = (sbyte)(within % 255 - 127);
            v.Material = (BlockType)(within % (1 + slab % 24));
            chunk[i] = v;
        }

        return chunk;
    }

    /// <summary>An entirely solid chunk: uniform, but the other sign from an empty one.</summary>
    static TerrainChunk Solid()
    {
        var chunk = new TerrainChunk(new ChunkIndex { x = 1, z = 1 });

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            var v = chunk[i];
            v.Density = Voxel.Minimum;
            v.Material = BlockType.BlockType_Stone;
            chunk[i] = v;
        }

        return chunk;
    }

    public static TheoryData<string, int> Cases => new()
    {
        { "realistic",   1100 },
        { "realistic",   0 },      // 0 means "exactly one worst-case slab" — filled in below
        { "edited",      1100 },
        { "adversarial", 1100 },
        { "adversarial", 0 },
        { "solid",       1100 },
        { "empty",       1100 },
    };

    static TerrainChunk Build(string name) => name switch
    {
        "realistic"   => Realistic(),
        "edited"      => Edited(),
        "adversarial" => Adversarial(),
        "solid"       => Solid(),
        _             => new TerrainChunk(new ChunkIndex { x = 0, z = 0 }),
    };

    /// <summary>
    /// Encode-then-decode must reproduce every voxel exactly. Streams through a payload budget the
    /// way the server does, so it also covers the resume-from-cursor path.
    ///
    /// The tight budget is <see cref="ChunkWire.MaxSlabBytes"/> — the worst-case slab — because that is
    /// the smallest budget at which streaming is guaranteed to make progress. Anything less can stall.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsEveryVoxel(string name, int budget)
    {
        var source = Build(name);
        var destination = new TerrainChunk(source.index);
        var buffer = new byte[budget == 0 ? ChunkWire.MaxSlabBytes : budget];

        int slabY = 0;
        int messages = 0;

        while (slabY < ChunkConstants.ChunkHeight)
        {
            var (slabCount, byteCount) = ChunkWire.Encode(source, slabY, buffer);

            Assert.True(slabCount > 0, "a single slab must always fit the budget, or streaming stalls");

            ChunkWire.Decode(destination, slabY, slabCount, buffer.AsSpan(0, byteCount));
            slabY += slabCount;
            messages++;
        }

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            Assert.Equal(source[i].Density, destination[i].Density);
            Assert.Equal(source[i].Material, destination[i].Material);
        }

        Assert.True(messages > 0);
    }

    /// <summary>
    /// The whole payload budget must be able to hold one worst-case slab, or a slab that compresses
    /// badly stalls that client's stream permanently.
    /// </summary>
    [Fact]
    public void WorstCaseSlabFitsOneMessage()
        => Assert.True(ChunkWire.MaxSlabBytes <= 1100, $"a slab can reach {ChunkWire.MaxSlabBytes} bytes");

    /// <summary>
    /// The size win, pinned. Uniform-slab collapsing was already worth ~8x; the plane encodings are
    /// worth ~3x on top of it, and the point of asserting it against the OLD formula is that a
    /// regression in either scheme shows up as a number rather than as slow terrain nobody traces.
    /// </summary>
    [Fact]
    public void PlaneEncodingsBeatRawSlabsSeveralTimesOver()
    {
        var chunk = Realistic();
        var buffer = new byte[64 * 1024];

        var (slabs, bytes) = ChunkWire.Encode(chunk, 0, buffer);
        Assert.Equal(ChunkConstants.ChunkHeight, slabs);           // whole column fits this buffer

        int mixed = MixedSlabs(chunk);
        int uniformOnly = mixed * (1 + ChunkWire.SlabVoxels * 2) + (ChunkConstants.ChunkHeight - mixed) * 3;

        Assert.True(bytes * 18 < uniformOnly * 10,
            $"expected at least 1.8x over uniform-collapse-only ({uniformOnly} bytes), got {bytes}");

        Assert.True(bytes < ChunkConstants.ChunkVolume * 2 / 16,
            $"expected under a sixteenth of raw ({ChunkConstants.ChunkVolume * 2} bytes), got {bytes}");
    }

    /// <summary>
    /// The advantage has to GROW with roughness, because the old cost scaled at 513 bytes per mixed
    /// slab and this one scales at roughly 59. That is what stops mountains costing more to stream
    /// than plains, and it is the property the whole format exists for.
    ///
    /// Note this cannot be tested against a perfectly flat chunk: every slab there is uniform, so both
    /// formats produce the same 384 bytes and the plane encodings contribute nothing. The best case is
    /// a tie by construction.
    /// </summary>
    [Fact]
    public void AdvantageGrowsAsTerrainGetsRougher()
    {
        var buffer = new byte[64 * 1024];

        (int now, int before, double ratio) Measure(TerrainChunk chunk)
        {
            int now = ChunkWire.Encode(chunk, 0, buffer).byteCount;

            int mixed = MixedSlabs(chunk);
            int before = mixed * (1 + ChunkWire.SlabVoxels * 2)
                       + (ChunkConstants.ChunkHeight - mixed) * 3;

            return (now, before, before / (double)now);
        }

        var gentle = Measure(Field((x, z) => x * 0.4f));            // ~6 voxels of span
        var rough  = Measure(Field((x, z) => (x + z) % 8 * 2.5f));  // ~17 voxels, changing every voxel

        Assert.True(gentle.ratio > 2.0,
            $"gentle slope: {gentle.now} vs {gentle.before} = {gentle.ratio:F2}x");

        Assert.True(rough.ratio > gentle.ratio,
            $"advantage must grow with roughness: gentle {gentle.ratio:F2}x, rough {rough.ratio:F2}x");
    }

    /// <summary>A chunk of nothing but air is the cheapest possible case: every slab uniform.</summary>
    [Fact]
    public void EmptyChunkIsTiny()
    {
        var chunk = new TerrainChunk(new ChunkIndex { x = 0, z = 0 });
        var buffer = new byte[64 * 1024];

        var (slabs, bytes) = ChunkWire.Encode(chunk, 0, buffer);

        Assert.Equal(ChunkConstants.ChunkHeight, slabs);
        Assert.Equal(ChunkConstants.ChunkHeight * 3, bytes);       // 3 bytes per uniform slab
    }

    // ---- helpers ----

    static int MixedSlabs(TerrainChunk chunk)
    {
        int mixed = 0;

        for (int slab = 0; slab < ChunkConstants.ChunkHeight; slab++)
        {
            int start = slab * ChunkConstants.ChunkSize;
            var first = chunk[start];

            for (int i = 1; i < ChunkConstants.ChunkSize; i++)
            {
                if (chunk[start + i].Density == first.Density &&
                    chunk[start + i].Material == first.Material) continue;

                mixed++;
                break;
            }
        }

        return mixed;
    }

    /// <summary>A chunk whose surface sits at a base height plus a per-column offset.</summary>
    static TerrainChunk Field(Func<int, int, float> columnOffset)
    {
        const float BaseHeight = 40.5f;

        var chunk = new TerrainChunk(new ChunkIndex { x = 0, z = 0 });

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            (int lx, int ly, int lz) = ChunkTransforms.LocalVoxelCoords(i);
            int worldY = ChunkConstants.WorldMinY + ly;

            float d = ChunkConstants.ClampToWorldFloor(
                worldY, worldY - (BaseHeight + columnOffset(lx, lz)));

            var vi = new Voxel { Distance = d };
            vi.Material = ChunkGenerator.DensityToMaterial(vi.Distance, d);
            chunk[i] = vi;
        }

        return chunk;
    }
}
