using System.Diagnostics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

[Trait("Category", "Benchmark")]
public class ScratchSurfaceCost
{
    [Fact]
    public void MeasureHighestSurface()
    {
        string mapPath = Path.Combine(RepoRoot(), "maps", "conquest", "runtime.dmap");
        var map = RuntimeMapSerializer.Load(mapPath);

        // Warm.
        for (int i = 0; i < 500; i++) SurfaceQuery.HighestSurface(map.Terrain, i, i);

        const int columns = 16384;   // one full 128x128 cache
        var sw = Stopwatch.StartNew();
        int hits = 0;
        for (int i = 0; i < columns; i++)
        {
            int x = (i % 128) * 3 - 190;
            int z = (i / 128) * 3 - 190;
            if (SurfaceQuery.HighestSurface(map.Terrain, x, z) is not null) hits++;
        }
        sw.Stop();
        Console.WriteLine(
            $"[cost] {columns} columns in {sw.Elapsed.TotalMilliseconds:F2} ms "
            + $"({sw.Elapsed.TotalMilliseconds / columns * 1000:F2} us/column, {hits} hit terrain); "
            + $"8 rows of 128 = {sw.Elapsed.TotalMilliseconds / 128 * 8:F2} ms/frame");
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DemiurgeSharp.slnx"))) d = d.Parent;
        return d!.FullName;
    }
}
