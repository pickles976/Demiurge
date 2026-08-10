using System.Diagnostics;
using System.Numerics;
using Demiurge.GameServer;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// What one cover query costs. The live profile made this the single worst spike in the game: a
/// 1-second combat window spent 4.2 ms per tick on FIVE queries, which is about 25 ms each — more
/// than a whole 33 ms server tick, and in singleplayer that tick sits inside the client's frame.
///
///     dotnet test --filter CoverBehaviorBenchmarks --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public class CoverBehaviorBenchmarks(ITestOutputHelper output)
{
    /// <summary>The map the profile was taken on — dug, trenched terrain rather than clean
    /// generated ground, which is the whole point: a raycast through a trench is not the same
    /// raycast as one over a hillside.</summary>
    static readonly Lazy<RuntimeMap> Map = new(() =>
        RuntimeMapSerializer.Load(Path.Combine(FindRepositoryRoot(), "maps", "npc-test", "runtime.dmap")));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("repository root");
    }

    [Fact]
    public void OneCoverQuery()
    {
        var map = Map.Value.Terrain;
        var cover = new CoverBehavior(map);
        var squad = new SquadBlackboard();

        // Spread the sample over the playable area rather than trusting one spot: cost varies
        // enormously with what the raycasts hit, and one lucky patch of open ground would report a
        // number the game never sees.
        var centre = Map.Value.Placements
            .Where(p => p.Kind == RuntimePlacementKind.Mob || p.Kind == RuntimePlacementKind.Flag)
            .Select(p => p.Position)
            .DefaultIfEmpty(Vector3.Zero)
            .First();

        var spawns = new List<Vector3>();
        for (int dx = -5; dx <= 5; dx++)
            for (int dz = -5; dz <= 5; dz++)
                spawns.Add(centre + new Vector3(dx * 9f, 0f, dz * 9f));

        var samples = new List<double>();
        var foundSamples = new List<double>();
        var rejectedSamples = new List<double>();
        int found = 0;
        for (int warm = 0; warm < 2; warm++)
            for (int i = 0; i < spawns.Count; i++)
            {
                var origin = SurfaceQuery.SurfacePosition(map, spawns[i].X, spawns[i].Z);
                var threatAt = SurfaceQuery.SurfacePosition(
                    map, spawns[(i + 1) % spawns.Count].X, spawns[(i + 1) % spawns.Count].Z);
                var mob = new ServerPlayer
                {
                    Id = 60_000,
                    IsMob = true,
                    Team = 1,
                    Move = new MoveState { Position = origin },
                    Status = new ServerObject
                    {
                        Has = NetComponents.Health,
                        Health = new HealthState { Current = 100, Max = 100 },
                    },
                };
                // Two threats, which is MaximumThreats and what a real firefight supplies.
                var threats = new[]
                {
                    new AiContact(60_100, threatAt, 0u, 1f),
                    new AiContact(60_101, threatAt + new Vector3(6f, 0f, 6f), 0u, 1f),
                };

                var timer = Stopwatch.StartNew();
                bool hit = cover.TryChoose(mob, threats, squad, out _);
                timer.Stop();
                if (warm == 0) continue;   // first pass warms the traversal paths
                samples.Add(timer.Elapsed.TotalMilliseconds);
                (hit ? foundSamples : rejectedSamples).Add(timer.Elapsed.TotalMilliseconds);
                if (hit) found++;
            }

        samples.Sort();
        output.WriteLine(
            $"CoverBehavior.TryChoose over {samples.Count} positions "
          + $"({found} found cover):");
        output.WriteLine($"  median {samples[samples.Count / 2]:F2} ms");
        output.WriteLine($"  p95    {samples[(int)(samples.Count * 0.95)]:F2} ms");
        output.WriteLine($"  worst  {samples[^1]:F2} ms");
        output.WriteLine($"  mean   {samples.Average():F2} ms   (server tick budget is 33 ms)");
        foundSamples.Sort();
        rejectedSamples.Sort();
        if (foundSamples.Count > 0)
            output.WriteLine(
                $"  found cover:    median {foundSamples[foundSamples.Count / 2]:F2}  "
              + $"worst {foundSamples[^1]:F2}  mean {foundSamples.Average():F2} ms");
        if (rejectedSamples.Count > 0)
            output.WriteLine(
                $"  no cover found: median {rejectedSamples[rejectedSamples.Count / 2]:F2}  "
              + $"worst {rejectedSamples[^1]:F2}  mean {rejectedSamples.Average():F2} ms");
    }
}
