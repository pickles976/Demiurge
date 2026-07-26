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

    /// <summary>
    /// Encode-then-decode must reproduce every voxel exactly. Streams through a payload budget the
    /// way the server does, so it also covers the resume-from-cursor path.
    /// </summary>
    [Theory]
    [InlineData(1100)]                  // the server's real budget
    [InlineData(ChunkWire.SlabVoxels * 2 + 1)]   // exactly one raw slab: worst-case fragmentation
    public void RoundTripsEveryVoxel(int budget)
    {
        var source = Realistic();
        var destination = new TerrainChunk(source.index);
        var buffer = new byte[budget];

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
            Assert.Equal(source.voxels[i].Density, destination.voxels[i].Density);
            Assert.Equal(source.voxels[i].Material, destination.voxels[i].Material);
        }

        Assert.True(messages > 0);
    }

    /// <summary>
    /// The compression is one trick — flagging slabs that are a single repeated voxel — and it's the
    /// only reason the fixed map is a few hundred KB instead of 2.3 MB. If this regresses, transfer
    /// size grows ~8x without anything appearing broken.
    /// </summary>
    [Fact]
    public void UniformSlabsCostFarLessThanRaw()
    {
        var chunk = Realistic();
        var buffer = new byte[64 * 1024];

        var (slabs, bytes) = ChunkWire.Encode(chunk, 0, buffer);

        Assert.Equal(ChunkConstants.ChunkHeight, slabs);           // whole column fits this buffer
        Assert.True(bytes < ChunkConstants.ChunkVolume * 2 / 4,
            $"expected well under a quarter of raw ({ChunkConstants.ChunkVolume * 2} bytes), got {bytes}");
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
}
