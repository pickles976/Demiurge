using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class NavigationSystemTests
{
    [Fact]
    public void NearbySquadMemberReusesTheSharedObjectiveRoute()
    {
        var map = FlatTerrain();
        var firstStart = CellAt(map, -4, 0);
        var secondStart = CellAt(map, -3, 1);
        var target = CellAt(map, 7, 0);
        var secondTarget = CellAt(map, 7, 2);
        using var navigation = new NavigationSystem(map);
        const long squadRoute = 0x0001_0002_0000_0003;

        long firstRequest = navigation.Request(
            mobId: 60_000,
            firstStart,
            new GoalNear(target, 1f),
            sharedRouteKey: squadRoute);
        var first = WaitFor(navigation, firstRequest);
        Assert.True(first.Path.ReachedGoal);

        long secondRequest = navigation.Request(
            mobId: 60_001,
            secondStart,
            new GoalNear(secondTarget, 0.6f),
            sharedRouteKey: squadRoute);
        var second = WaitFor(navigation, secondRequest);

        Assert.True(second.Path.ReachedGoal);
        Assert.True(new GoalNear(secondTarget, 0.6f).IsInGoal(second.Path.Waypoints[^1].Cell));
        Assert.True(navigation.SnapshotMetrics().SharedRouteReuses >= 1);
    }

    [Fact]
    public void NearbySquadMemberCanReuseAPartialObjectiveTrunk()
    {
        var map = FlatTerrain();
        var firstStart = CellAt(map, -10, 0);
        var secondStart = CellAt(map, -9, 1);
        var target = CellAt(map, 18, 0);
        using var navigation = new NavigationSystem(
            map,
            NavSearchOptions.Default with
            {
                PrimaryBudget = TimeSpan.FromSeconds(1),
                FailureBudget = TimeSpan.FromSeconds(1),
                MaximumExpandedNodes = 6,
                MinimumPartialDistance = 0f,
            });
        const long squadRoute = 0x0001_0002_0000_0004;

        var first = WaitFor(
            navigation,
            navigation.Request(
                mobId: 60_002,
                firstStart,
                new GoalNear(target, 1f),
                sharedRouteKey: squadRoute));
        Assert.False(first.Path.ReachedGoal);
        Assert.True(first.Path.Waypoints.Count >= 2);

        var second = WaitFor(
            navigation,
            navigation.Request(
                mobId: 60_003,
                secondStart,
                new GoalNear(target, 1f),
                sharedRouteKey: squadRoute));

        Assert.False(second.Path.ReachedGoal);
        Assert.True(second.Path.Waypoints.Count >= 2);
        Assert.True(navigation.SnapshotMetrics().SharedRouteReuses >= 1);
    }

    [Fact]
    public void SquadPrefetchExtendsTheSharedPartialTrunk()
    {
        var map = FlatTerrain();
        var start = CellAt(map, -10, 0);
        var target = CellAt(map, 18, 0);
        var options = NavSearchOptions.Default with
        {
            PrimaryBudget = TimeSpan.FromSeconds(1),
            FailureBudget = TimeSpan.FromSeconds(1),
            MaximumExpandedNodes = 6,
            MinimumPartialDistance = 0f,
        };
        using var navigation = new NavigationSystem(map, options, workerCount: 2);
        const long squadRoute = 0x0001_0002_0000_0005;

        var first = WaitFor(
            navigation,
            navigation.Request(
                mobId: 60_004,
                start,
                new GoalNear(target, 1f),
                sharedRouteKey: squadRoute));
        Assert.False(first.Path.ReachedGoal);
        var firstEnd = first.Path.Waypoints[^1].Cell;

        var extended = WaitFor(
            navigation,
            navigation.Request(
                mobId: 60_005,
                start,
                new GoalNear(target, 1f),
                sharedRouteKey: squadRoute,
                priority: NavigationPriority.Prefetch));

        Assert.True(
            GoalPosition.Distance(extended.Path.Waypoints[^1].Cell, target)
            < GoalPosition.Distance(firstEnd, target));
        Assert.True(navigation.SnapshotMetrics().SharedRouteReuses >= 1);
    }

    private static NavigationSystem.PathResult WaitFor(
        NavigationSystem navigation,
        long requestId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (navigation.TryGetCompleted(out var result)
                && result.RequestId == requestId)
                return result;
            Thread.Sleep(1);
        }
        throw new TimeoutException($"Navigation request {requestId} did not complete");
    }

    private static NavCell CellAt(ChunkMap map, int x, int z)
    {
        Assert.True(NavTraversal.TryFindStandable(
            map,
            x,
            z,
            aroundY: 12,
            below: 4,
            above: 4,
            out var cell,
            out _));
        return cell;
    }

    private static ChunkMap FlatTerrain()
    {
        const float surface = 12.5f;
        var map = new ChunkMap();
        for (int cz = -1; cz <= 1; cz++)
            for (int cx = -1; cx <= 1; cx++)
            {
                var chunk = new TerrainChunk(new ChunkIndex { x = cx, z = cz });
                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    var (_, y, _) = ChunkTransforms.LocalVoxelCoords(i);
                    int worldY = ChunkConstants.WorldMinY + y;
                    float distance = ChunkConstants.ClampToWorldFloor(
                        worldY,
                        worldY - surface);
                    var voxel = new Voxel { Distance = distance };
                    voxel.Material = ChunkGenerator.DensityToMaterial(
                        voxel.Distance,
                        distance);
                    chunk[i] = voxel;
                }
                map.Insert(chunk);
            }
        return map;
    }
}
