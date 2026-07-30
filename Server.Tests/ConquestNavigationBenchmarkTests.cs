using System.Diagnostics;
using Xunit.Abstractions;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public sealed class ConquestNavigationBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ThirtyTwoNpcInitialObjectiveRoutes()
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(
            Path.Combine(root, "maps", "conquest", "runtime.dmap"));
        var spawnPlan = InitialTeamSpawnPlan.Create(
            map.Placements,
            playerTeam: 1,
            npcsPerTeam: 16);
        var flags = map.Placements
            .Where(placement => placement.Kind == RuntimePlacementKind.Flag)
            .ToArray();
        Assert.Equal(32, spawnPlan.NpcSpawns.Count);
        Assert.NotEmpty(flags);

        using var navigation = new NavigationSystem(map.Terrain, workerCount: 8);
        var expected = new HashSet<long>();
        var started = Stopwatch.StartNew();
        for (int i = 0; i < spawnPlan.NpcSpawns.Count; i++)
        {
            var spawn = spawnPlan.NpcSpawns[i];
            int squad = i % 16 / SquadBlackboard.MaximumMembers;
            var destination = flags.MaxBy(flag =>
                HorizontalDistanceSquared(spawn.Position, flag.Position));
            Assert.True(TryCellAt(map.Terrain, spawn.Position, out var start));
            Assert.True(TryCellAt(map.Terrain, destination.Position, out var target));
            long sharedKey =
                ((long)(uint)spawn.Team << 48)
                ^ ((long)(uint)squad << 32)
                ^ (uint)(squad + 1);
            expected.Add(navigation.Request(
                (ushort)(60_000 + i),
                start,
                new GoalNear(target, 1.25f),
                allowJump: true,
                allowDig: true,
                sharedRouteKey: sharedKey));
        }

        var queueTimes = new List<long>();
        int usefulPaths = 0;
        var deadline = Stopwatch.StartNew();
        while (expected.Count > 0 && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (!navigation.TryGetCompleted(out var result))
            {
                Thread.Sleep(1);
                continue;
            }
            if (!expected.Remove(result.RequestId)) continue;
            queueTimes.Add(result.QueueMicroseconds);
            usefulPaths += result.Path.Waypoints.Count >= 2 ? 1 : 0;
        }
        started.Stop();

        Assert.Empty(expected);
        Assert.Equal(32, usefulPaths);
        queueTimes.Sort();
        long p50 = queueTimes[(int)MathF.Ceiling(queueTimes.Count * 0.50f) - 1];
        long p95 = queueTimes[(int)MathF.Ceiling(queueTimes.Count * 0.95f) - 1];
        var metrics = navigation.SnapshotMetrics();
        output.WriteLine(
            $"32 conquest paths: wall {started.Elapsed.TotalMilliseconds:0} ms; "
            + $"queue p50/p95 {p50 / 1000f:0.0}/{p95 / 1000f:0.0} ms; "
            + $"{metrics.SharedRouteReuses} shared-route reuses; "
            + $"{metrics.CacheHits} traversal-cache hits; "
            + $"{metrics.ExpandedNodes} expanded nodes");

        Assert.True(metrics.SharedRouteReuses >= 16);
        Assert.True(p95 < 500_000, $"Queue p95 was {p95 / 1000f:0.0} ms");
    }

    private static bool TryCellAt(
        ChunkMap terrain,
        System.Numerics.Vector3 position,
        out NavCell cell)
    {
        var surface = SurfaceQuery.SurfacePosition(terrain, position.X, position.Z);
        return NavTraversal.TryFindNearestStandable(
            terrain,
            surface,
            horizontalRadius: 8,
            out cell);
    }

    private static float HorizontalDistanceSquared(
        System.Numerics.Vector3 a,
        System.Numerics.Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find DemiurgeSharp.slnx");
    }
}
