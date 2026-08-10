using System.Diagnostics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// What a navigation standability probe costs. This is the unit of work A* is actually made of, and
/// it is the reason a 17 m path measured 115-167 ms in a live run: one uncached probe is
/// `2 x TrySampleRaw` plus up to four `TryDeepestContact` resolve passes, each three capsule spheres
/// of 56 trilinear corner reads — up to 688 reads for one cell.
///
/// Separate from <see cref="MovementBenchmarks"/> because the two answer different questions.
/// That one measures `PlayerMovement.Step`, which the live `ai stats` line shows is a minority of
/// the cost it reports: the `movement` timer wraps the whole per-mob AI tick (`MobSystem.Step`), and
/// most of the rest of it lands here.
///
///     dotnet test -c Release --filter StandabilityBenchmarks --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public class StandabilityBenchmarks(ITestOutputHelper output)
{
    static readonly Lazy<ChunkMap> World = new(() =>
    {
        var map = new ChunkMap();
        WorldGen.Generate(map);
        return map;
    });

    static double Us(long ticks, int n) => ticks * 1_000_000.0 / Stopwatch.Frequency / n;

    [Fact]
    public void ProbeAndTheSearchItImplies()
    {
        var map = World.Value;
        var cache = new NavProbeCache(map);

        // Cells spread over a wide area rather than one column, because a probe's cost is dominated
        // by fetching voxels it has not touched yet — measuring a single hot cell would measure the
        // L1 cache instead of the work.
        var cells = new List<(int X, int Y, int Z)>();
        for (int x = 0; x < 64; x++)
            for (int z = 0; z < 64; z++)
                if (SurfaceQuery.HighestSurfaceY(map, x, z) is { } surfaceY)
                    cells.Add((x, (int)MathF.Floor(surfaceY), z));

        Assert.NotEmpty(cells);

        // Warm, and prove the sweep finds real standable ground rather than measuring early rejects.
        int standable = 0;
        foreach (var cell in cells)
        {
            cache.Reset(map);
            if (NavTraversal.Standable(cache, cell.X, cell.Y, cell.Z, out _)) standable++;
        }

        Assert.True(standable > cells.Count / 4, $"only {standable} of {cells.Count} cells standable");

        // Uncached: Reset before every probe, which is what a search sees the first time it expands
        // into a cell.
        long t0 = Stopwatch.GetTimestamp();
        foreach (var cell in cells)
        {
            cache.Reset(map);
            NavTraversal.Standable(cache, cell.X, cell.Y, cell.Z, out _);
        }
        double coldUs = Us(Stopwatch.GetTimestamp() - t0, cells.Count);

        // Cached: one memo across the whole sweep, which is what a search sees on re-probes. The gap
        // between the two is what NavProbeCache is worth, and why its per-query Reset matters.
        cache.Reset(map);
        foreach (var cell in cells) NavTraversal.Standable(cache, cell.X, cell.Y, cell.Z, out _);

        long t1 = Stopwatch.GetTimestamp();
        foreach (var cell in cells) NavTraversal.Standable(cache, cell.X, cell.Y, cell.Z, out _);
        double warmUs = Us(Stopwatch.GetTimestamp() - t1, cells.Count);

        // The column walk underneath a lot of this, measured on its own.
        long t2 = Stopwatch.GetTimestamp();
        foreach (var cell in cells) SurfaceQuery.HighestSurfaceY(map, cell.X, cell.Z);
        double surfaceUs = Us(Stopwatch.GetTimestamp() - t2, cells.Count);

        output.WriteLine($"cells sampled              {cells.Count,8}   ({standable} standable)");
        output.WriteLine($"Standable, cold cache      {coldUs,8:F2} us   "
                       + $"(527-expansion trench crossing -> {coldUs * 527 / 1000:F0} ms)");
        output.WriteLine($"Standable, warm cache      {warmUs,8:F2} us");
        output.WriteLine($"SurfaceQuery.HighestSurfaceY {surfaceUs,6:F2} us");
    }
}
