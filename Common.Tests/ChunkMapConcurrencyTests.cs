namespace Demiurge.Tests;

/// <summary>
/// Meshing reads <see cref="ChunkMap"/> from worker threads while the main thread inserts arriving
/// chunks, so its lookup has to survive concurrent insert. Guarding that with a test because the failure
/// mode is not a clean exception — a plain Dictionary resized under a reader corrupts buckets, which
/// surfaces later as a lookup that returns the wrong chunk or spins forever.
/// </summary>
public class ChunkMapConcurrencyTests
{
    [Fact]
    public void LookupsSurviveConcurrentInserts()
    {
        const int Chunks = 400;
        const int Readers = 4;

        var map = new ChunkMap();
        var failure = new List<Exception>();
        using var writerDone = new ManualResetEventSlim();

        void Reader()
        {
            try
            {
                // Hammer lookups, including through the world-coordinate path meshing actually uses.
                while (!writerDone.IsSet)
                    for (int i = 0; i < Chunks; i++)
                    {
                        map.Has(new ChunkIndex { x = i, z = 0 });
                        map.Get(new ChunkIndex { x = i, z = 0 });
                        map.TryGetVoxel(i * ChunkConstants.ChunkWidth, 40, 0, out _);
                    }
            }
            catch (Exception e)
            {
                lock (failure) failure.Add(e);
            }
        }

        var readers = new Thread[Readers];
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = new Thread(Reader) { IsBackground = true };
            readers[i].Start();
        }

        for (int i = 0; i < Chunks; i++)
            map.Insert(new TerrainChunk(new ChunkIndex { x = i, z = 0 }));

        writerDone.Set();
        foreach (var reader in readers) Assert.True(reader.Join(TimeSpan.FromSeconds(10)), "a reader hung");

        Assert.Empty(failure);

        // Every insert has to be visible afterwards, not merely not-crashing.
        for (int i = 0; i < Chunks; i++)
            Assert.NotNull(map.Get(new ChunkIndex { x = i, z = 0 }));
    }
}
