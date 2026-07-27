namespace Demiurge.Tests;

/// <summary>
/// The bulk terrain stream's framing. Worth pinning harder than the old Riptide path was: over UDP a
/// bad datagram was one bad chunk, but over a stream a wrong length desynchronises everything after it,
/// so the failure is silent, total, and permanent.
/// </summary>
public class ChunkTransportTests
{
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(7, -13, 2048)]
    [InlineData(-20, 20, 65920)]
    [InlineData(int.MinValue, int.MaxValue, 3)]
    public void HeaderRoundTrips(int chunkX, int chunkZ, int length)
    {
        var buffer = new byte[ChunkTransport.HeaderBytes];
        var index = new ChunkIndex { x = chunkX, z = chunkZ };

        ChunkTransport.WriteHeader(buffer, index, length);

        Assert.True(ChunkTransport.TryReadHeader(buffer, out var read, out int readLength));
        Assert.Equal(index, read);
        Assert.Equal(length, readLength);
    }

    /// <summary>
    /// A length this protocol could not have produced means the stream is desynchronised, and the reader
    /// has to refuse it rather than allocate whatever it was told to.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ImpossibleLengthsAreRejected(int length)
    {
        var buffer = new byte[ChunkTransport.HeaderBytes];
        ChunkTransport.WriteHeader(buffer, new ChunkIndex { x = 1, z = 1 }, length);

        Assert.False(ChunkTransport.TryReadHeader(buffer, out _, out _));
    }

    [Fact]
    public void MaximumLengthIsAccepted()
    {
        var buffer = new byte[ChunkTransport.HeaderBytes];
        ChunkTransport.WriteHeader(buffer, new ChunkIndex { x = 0, z = 0 }, ChunkTransport.MaxPayloadBytes);

        Assert.True(ChunkTransport.TryReadHeader(buffer, out _, out int length));
        Assert.Equal(ChunkTransport.MaxPayloadBytes, length);
    }

    /// <summary>
    /// A worst-case column must fit the buffer the writer sizes from <see cref="ChunkTransport.MaxPayloadBytes"/>,
    /// or a badly-compressing chunk truncates and the client decodes garbage.
    /// </summary>
    [Fact]
    public void BufferFitsAWholeColumnEvenAtWorstCase()
        => Assert.True(ChunkTransport.MaxPayloadBytes >= ChunkWire.MaxSlabBytes * ChunkConstants.ChunkHeight);

    /// <summary>
    /// The real thing: several chunks written back to back through one stream and read out again. This is
    /// what proves frames don't bleed into each other — the one-chunk case can pass while the framing is
    /// off by a constant.
    /// </summary>
    [Fact]
    public void ManyChunksSurviveOneStream()
    {
        var sources = new[]
        {
            ChunkGenerator.GenerateChunk(new ChunkIndex { x = 0, z = 0 }),
            ChunkGenerator.GenerateChunk(new ChunkIndex { x = -7, z = 3 }),
            new TerrainChunk(new ChunkIndex { x = 1, z = 1 }),              // all air: shortest frame
            Adversarial(new ChunkIndex { x = 2, z = -2 }),                  // longest frame
            ChunkGenerator.GenerateChunk(new ChunkIndex { x = 5, z = 5 }),
        };

        var stream = new MemoryStream();
        var payload = new byte[ChunkTransport.MaxPayloadBytes];
        var header = new byte[ChunkTransport.HeaderBytes];

        foreach (var chunk in sources)
        {
            var (slabs, length) = ChunkWire.Encode(chunk, 0, payload);

            Assert.Equal(ChunkConstants.ChunkHeight, slabs);   // a frame is a WHOLE column

            ChunkTransport.WriteHeader(header, chunk.index, length);
            stream.Write(header);
            stream.Write(payload, 0, length);
        }

        stream.Position = 0;

        foreach (var expected in sources)
        {
            stream.ReadExactly(header);

            Assert.True(ChunkTransport.TryReadHeader(header, out var index, out int length));
            Assert.Equal(expected.index, index);

            var received = new byte[length];
            stream.ReadExactly(received);

            var decoded = new TerrainChunk(index);
            ChunkWire.Decode(decoded, 0, ChunkConstants.ChunkHeight, received);

            for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            {
                Assert.Equal(expected.voxels[i].Density, decoded.voxels[i].Density);
                Assert.Equal(expected.voxels[i].Material, decoded.voxels[i].Material);
            }
        }

        Assert.Equal(stream.Length, stream.Position);   // nothing left over, nothing over-read
    }

    /// <summary>Hostile to every compression path, so it produces the longest frames the writer can emit.</summary>
    static TerrainChunk Adversarial(ChunkIndex index)
    {
        var chunk = new TerrainChunk(index);

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            int slab = i / ChunkConstants.ChunkSize;
            int within = i % ChunkConstants.ChunkSize;

            chunk.voxels[i].Density = (sbyte)(within % 255 - 127);
            chunk.voxels[i].Material = (BlockType)(within % (1 + slab % 24));
        }

        return chunk;
    }
}
