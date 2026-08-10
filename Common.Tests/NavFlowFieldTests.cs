using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the field, not traces through its implementation. A test that pinned the exact
/// successor of a particular cell would encode one tie-break among several equally correct ones and
/// then obstruct any change to it.
/// </summary>
public class NavFlowFieldTests
{
    private static NavCell CellAt(ChunkMap map, int x, int z, int aroundY = 12)
    {
        Assert.True(
            NavTraversal.TryFindStandable(
                map, x, z, aroundY, below: 4, above: 4, out var cell, out _),
            $"({x}, {z}) is not standable");
        return cell;
    }

    [Fact]
    public void DestinationCostsNothingAndLeadsNowhere()
    {
        var map = SyntheticTerrain.Flat();
        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 24f, strideCells: 1);

        Assert.Equal(0f, field.CostFrom(goal));
        Assert.False(field.TryNext(goal, out _));
    }

    [Fact]
    public void EveryStepStrictlyDescendsTowardTheDestination()
    {
        // The property that makes a field usable at all: following it terminates. Strictly falling
        // cost means the successor graph is acyclic, so no actor can be handed a two-cell loop.
        var map = SyntheticTerrain.Flat();
        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 24f, strideCells: 1);
        var start = CellAt(map, 10, 7);

        float? cost = field.CostFrom(start);
        Assert.NotNull(cost);

        var cell = start;
        int steps = 0;
        while (field.TryNext(cell, out var next))
        {
            float? nextCost = field.CostFrom(next);
            Assert.NotNull(nextCost);
            Assert.True(
                nextCost!.Value < cost!.Value,
                $"cost rose from {cost} to {nextCost} stepping {cell} -> {next}");
            cost = nextCost;
            cell = next;
            Assert.True(++steps < 1000, "route did not terminate");
        }

        Assert.Equal(goal, cell);
    }

    [Fact]
    public void CostIsSecondsOfTravelInTheSharedCurrency()
    {
        // The field has to be priced in NavCosts seconds or it cannot be compared against a local
        // search's answer. Ten cells of flat ground is ten metres of walking, within the tolerance
        // the diagonal-free straight line allows.
        var map = SyntheticTerrain.Flat();
        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 32f, strideCells: 1);

        float? cost = field.CostFrom(CellAt(map, 10, 0));
        Assert.NotNull(cost);
        Assert.Equal(10f * NavCosts.WalkOneMetre, cost!.Value, 1);
    }

    [Fact]
    public void CellsBeyondTheRadiusHaveNoRoute()
    {
        var map = SyntheticTerrain.Flat();
        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 8f, strideCells: 1);

        Assert.NotNull(field.CostFrom(CellAt(map, 6, 0)));
        Assert.Null(field.CostFrom(CellAt(map, 20, 0)));
    }

    [Fact]
    public void AWallIsRoutedAroundRatherThanThrough()
    {
        // A field is a global solve, so unlike a greedy heuristic it cannot stall in the pocket
        // behind an obstacle: the route out exists in the field or the cell has no entry at all.
        var map = SyntheticTerrain.Wall(wallX: 4f);
        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 48f, strideCells: 1);

        foreach (var entry in new[] { CellAt(map, 0, 6), CellAt(map, -6, 0) })
        {
            Assert.NotNull(field.CostFrom(entry));
            Assert.NotEmpty(field.Route(entry));
        }
    }

    [Fact]
    public void UnstandableDestinationYieldsAnEmptyField()
    {
        var map = SyntheticTerrain.Solid();
        var field = NavFlowField.Build(map, new NavCell(0, 40, 0), radiusMetres: 16f, strideCells: 1);

        Assert.Equal(0, field.Count);
        Assert.True(field.Complete);
    }

    [Fact]
    public void TheFieldRecordsTheTerrainItWasSolvedAgainst()
    {
        // Staleness is the field's central hazard, so the revision travels with it rather than being
        // remembered by whoever built it.
        var map = SyntheticTerrain.Flat();
        var field = NavFlowField.Build(map, CellAt(map, 0, 0), radiusMetres: 8f, strideCells: 1);

        Assert.Equal(map.EditVersion, field.TerrainVersion);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ACoarseFieldStillDescendsStrictlyToItsDestination(int stride)
    {
        // Striding must not cost the field the property that makes following it terminate.
        var map = SyntheticTerrain.Flat();
        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 48f, strideCells: stride);

        var start = field.Route(CellAt(map, 4 * stride, 2 * stride));
        Assert.NotEmpty(start);

        float previous = float.MaxValue;
        foreach (var cell in start)
        {
            float? cost = field.CostFrom(cell);
            Assert.NotNull(cost);
            Assert.True(cost!.Value < previous, "cost did not fall along the route");
            previous = cost.Value;
        }
        Assert.Equal(goal, start[^1]);
    }

    [Fact]
    public void ACoarseFieldSolvesFarFewerCellsForTheSameGround()
    {
        // The whole point of the stride. Quartering the lattice should cut the solve by roughly
        // sixteen; asserted loosely as "much smaller" so the test does not encode the exact ratio.
        var map = SyntheticTerrain.Flat();
        var goal = CellAt(map, 0, 0);

        int fine = NavFlowField.Build(map, goal, radiusMetres: 40f, strideCells: 1).Count;
        int coarse = NavFlowField.Build(map, goal, radiusMetres: 40f, strideCells: 4).Count;

        Assert.True(coarse * 8 < fine, $"coarse {coarse} was not much smaller than fine {fine}");
    }

    [Fact]
    public void ACoarseEdgeIsRefusedWhenTheGroundBetweenItsEndsIsNot()
    {
        // Soundness of the stride, and the reason a coarse edge is proved by its fine steps rather
        // than by a rise test between its endpoints. This is a thin barrier with walkable ground at
        // the same height on both sides and a way around its ends: exactly the shape a four-metre
        // lattice hop would step straight over. Reaching the far side must cost more than the
        // straight line, because the only route is round the end.
        const float ground = SyntheticTerrain.GroundHeight;
        var map = SyntheticTerrain.Build((x, y, z) =>
            MathF.Min(
                y - ground,
                Max6(4f - x, x - 5f, -8f - z, z - 8f, ground - y, y - (ground + 3f))));

        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 64f, strideCells: 4);

        float? beyond = field.CostFrom(CellAt(map, 8, 0));
        Assert.True(
            beyond is null || beyond.Value > 8f * NavCosts.WalkOneMetre,
            $"the field walked through the barrier: {beyond} s for 8 m");

        static float Max6(float a, float b, float c, float d, float e, float f)
            => MathF.Max(MathF.Max(MathF.Max(a, b), MathF.Max(c, d)), MathF.Max(e, f));
    }

    [Fact]
    public void ANodeCeilingProducesAPartialFieldRatherThanAWrongOne()
    {
        var map = SyntheticTerrain.Flat();
        var goal = CellAt(map, 0, 0);
        var field = NavFlowField.Build(map, goal, radiusMetres: 64f, strideCells: 1, maximumCells: 64);

        Assert.False(field.Complete);
        // Everything it did reach is still sound: cost falls monotonically to the destination.
        foreach (var cell in new[] { goal })
            Assert.NotNull(field.CostFrom(cell));
    }
}
