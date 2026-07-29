using System.Numerics;

namespace Demiurge.Tests;

public class NavigationTests
{
    private static readonly NavSearchOptions CompleteSearch = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        100_000,
        0f);

    [Theory]
    [InlineData(0, 12, 0)]
    [InlineData(-12345, 64, 54321)]
    [InlineData(33_000_000, 127, -33_000_000)]
    public void PackedCellsRoundTrip(int x, int y, int z)
    {
        var cell = new NavCell(x, y, z);
        Assert.Equal(cell, NavCell.FromKey(cell.Key));
    }

    [Fact]
    public void HeapIsStableAndSupportsDecreaseKey()
    {
        var heap = new NavHeap();
        heap.EnqueueOrDecrease(1, 5f);
        heap.EnqueueOrDecrease(2, 2f);
        heap.EnqueueOrDecrease(3, 2f);
        heap.EnqueueOrDecrease(1, 1f);

        Assert.True(heap.TryDequeue(out long first));
        Assert.True(heap.TryDequeue(out long second));
        Assert.True(heap.TryDequeue(out long third));
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
        Assert.False(heap.TryDequeue(out _));
    }

    [Fact]
    public void FlatPathHasExpectedDistanceCost()
    {
        var map = SyntheticTerrain.Flat();
        var start = CellAt(map, -5, 0);
        var target = CellAt(map, 5, 0);

        var path = NavSearch.Find(map, start, new GoalPosition(target), CompleteSearch);

        Assert.True(path.ReachedGoal);
        Assert.Equal(start, path.Waypoints[0].Cell);
        Assert.Equal(target, path.Waypoints[^1].Cell);
        Assert.Equal(10f / PlayerMovement.WalkSpeed, path.Cost, 4);
    }

    [Fact]
    public void WalkableSlopeClimbsAndSteepSlopeIsRejected()
    {
        var walkable = SyntheticTerrain.Slope(30f);
        var start = CellAt(walkable, -4, 0);
        var target = CellAt(walkable, 4, 0);

        var path = NavSearch.Find(
            walkable,
            start,
            new GoalPosition(target),
            CompleteSearch);

        Assert.True(path.ReachedGoal);
        Assert.True(path.Waypoints[^1].Position.Y > path.Waypoints[0].Position.Y);

        var steep = SyntheticTerrain.Slope(70f);
        Assert.False(TryCellAt(steep, 0, 0, out _));
    }

    [Fact]
    public void FiniteWallRoutesAroundInsteadOfCuttingCorners()
    {
        var wallCentre = new Vector3(0f, SyntheticTerrain.GroundHeight + 5f, 0f);
        var wallExtent = new Vector3(0.6f, 8f, 2.5f);
        var map = SyntheticTerrain.Build((x, y, z) =>
            MathF.Min(
                y - SyntheticTerrain.GroundHeight,
                TerrainEdits.BoxDistance(
                    new Vector3(x, y, z) - wallCentre,
                    wallExtent)));
        var start = CellAt(map, -5, 0);
        var target = CellAt(map, 5, 0);

        var path = NavSearch.Find(map, start, new GoalPosition(target), CompleteSearch);

        Assert.True(path.ReachedGoal);
        Assert.True(
            path.Waypoints.Any(point => Math.Abs(point.Cell.Z) >= 3)
            || path.Waypoints.Any(point => point.Action == NavAction.Jump));
        Assert.True(path.Cost > 10f / PlayerMovement.WalkSpeed);
    }

    [Fact]
    public void SearchUsesAuthoritativeJumpAcrossOneMetreTrench()
    {
        var trenchCentre = new Vector3(
            0.5f,
            SyntheticTerrain.GroundHeight + 4f,
            0f);
        var trenchExtent = new Vector3(0.55f, 8f, 100f);
        var map = SyntheticTerrain.Build((x, y, z) =>
            MathF.Max(
                y - SyntheticTerrain.GroundHeight,
                -TerrainEdits.BoxDistance(
                    new Vector3(x, y, z) - trenchCentre,
                    trenchExtent)));
        var start = CellAt(map, -2, 0);
        var target = CellAt(map, 2, 0);

        var path = NavSearch.Find(map, start, new GoalPosition(target), CompleteSearch);

        Assert.True(path.ReachedGoal);
        Assert.Contains(path.Waypoints, waypoint => waypoint.Action == NavAction.Jump);
    }

    [Fact]
    public void SolidStartFailsImmediately()
    {
        var map = SyntheticTerrain.Solid();
        var start = new NavCell(0, 12, 0);

        var path = NavSearch.Find(map, start, new GoalPosition(start), CompleteSearch);

        Assert.False(path.ReachedGoal);
        Assert.Empty(path.Waypoints);
        Assert.Equal(0, path.ExpandedNodes);
    }

    [Fact]
    public void OneColumnCanContainDistinctStandableLevels()
    {
        const float slabLow = 22f;
        const float slabHigh = 23.5f;
        var map = SyntheticTerrain.Build((x, y, z) =>
            MathF.Min(
                y - SyntheticTerrain.GroundHeight,
                MathF.Max(slabLow - y, y - slabHigh)));

        var ground = CellAt(map, 0, 0, aroundY: 12);
        var slab = CellAt(map, 0, 0, aroundY: 23);

        Assert.NotEqual(ground, slab);
        Assert.Equal(ground.X, slab.X);
        Assert.Equal(ground.Z, slab.Z);
        Assert.True(NavTraversal.Position(map, slab).Y > NavTraversal.Position(map, ground).Y);
    }

    [Fact]
    public void SearchIsDeterministic()
    {
        var map = SyntheticTerrain.Flat();
        var start = CellAt(map, -8, -7);
        var target = CellAt(map, 8, 6);

        var first = NavSearch.Find(map, start, new GoalPosition(target), CompleteSearch);
        var second = NavSearch.Find(map, start, new GoalPosition(target), CompleteSearch);

        Assert.Equal(
            first.Waypoints.Select(point => point.Cell),
            second.Waypoints.Select(point => point.Cell));
        Assert.Equal(first.Cost, second.Cost);
    }

    [Fact]
    public void ExpansionBudgetReturnsAUsefulPartialPath()
    {
        var map = SyntheticTerrain.Flat();
        var start = CellAt(map, -12, 0);
        var target = CellAt(map, 12, 0);
        var options = new NavSearchOptions(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            MaximumExpandedNodes: 5,
            MinimumPartialDistance: 0f);

        var path = NavSearch.Find(map, start, new GoalPosition(target), options);

        Assert.False(path.ReachedGoal);
        Assert.True(path.Waypoints.Count > 1);
        Assert.True(
            GoalPosition.Distance(path.Waypoints[^1].Cell, target)
            < GoalPosition.Distance(start, target));
        Assert.Equal(5, path.ExpandedNodes);
    }

    [Fact]
    public void GoalNearAndAwayUseBoundedDistance()
    {
        var origin = new NavCell(0, 12, 0);
        var nearby = new NavCell(2, 12, 0);

        Assert.True(new GoalNear(origin, 2f).IsInGoal(nearby));
        Assert.False(new GoalNear(origin, 1.9f).IsInGoal(nearby));
        Assert.True(new GoalAwayFrom(origin, 2f).IsInGoal(nearby));
        Assert.False(new GoalAwayFrom(origin, 2.1f).IsInGoal(nearby));
    }

    private static NavCell CellAt(
        ChunkMap map,
        int x,
        int z,
        int aroundY = (int)SyntheticTerrain.GroundHeight)
    {
        Assert.True(
            NavTraversal.TryFindStandable(
                map,
                x,
                z,
                aroundY,
                ChunkConstants.ChunkHeight,
                ChunkConstants.ChunkHeight,
                out var cell,
                out _),
            $"No standable navigation cell at ({x}, {z}) near Y={aroundY}");
        return cell;
    }

    private static bool TryCellAt(ChunkMap map, int x, int z, out NavCell cell)
        => NavTraversal.TryFindStandable(
            map,
            x,
            z,
            (int)SyntheticTerrain.GroundHeight,
            ChunkConstants.ChunkHeight,
            ChunkConstants.ChunkHeight,
            out cell,
            out _);
}
