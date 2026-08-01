using System.Numerics;

namespace Demiurge.Tests;

public class NavigationTests
{
    private sealed class TerrainChangingGoal(ChunkMap map, NavCell cell) : INavGoal
    {
        public bool IsInGoal(NavCell candidate)
        {
            SetCellToAir(map, cell);
            return candidate == cell;
        }

        public float Heuristic(NavCell candidate) => 0f;
    }

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
    public void TerrainEditDuringReconstructionReturnsFailedPath()
    {
        var map = SyntheticTerrain.Flat();
        Assert.True(NavTraversal.TryFindStandable(
            map,
            0,
            0,
            aroundY: 12,
            below: 4,
            above: 4,
            out var start,
            out _));

        var path = NavSearch.Find(
            map,
            start,
            new TerrainChangingGoal(map, start));

        Assert.Empty(path.Waypoints);
        Assert.False(path.ReachedGoal);
    }

    private static void SetCellToAir(ChunkMap map, NavCell cell)
    {
        var chunk = Assert.IsType<TerrainChunk>(
            map.Get(ChunkTransforms.ChunkAt(cell.X, cell.Z)));
        for (int y = cell.Y - 2; y <= cell.Y + 3; y++)
            chunk[ChunkTransforms.WorldVoxelIndex(cell.X, y, cell.Z)] =
                Voxel.OutsideAbove;
    }

    [Fact]
    public void SharpOneMetreLedgeUsesJumpInsteadOfMisclassifiedWalk()
    {
        var platformCentre = new Vector3(
            0.5f,
            SyntheticTerrain.GroundHeight + 0.5f,
            0f);
        var platformExtent = new Vector3(2f, 0.5f, 100f);
        var map = SyntheticTerrain.Build((x, y, z) =>
            MathF.Min(
                y - SyntheticTerrain.GroundHeight,
                TerrainEdits.BoxDistance(
                    new Vector3(x, y, z) - platformCentre,
                    platformExtent)));
        var start = CellAt(map, -3, 0);
        var target = CellAt(map, 1, 0);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch);

        Assert.True(path.ReachedGoal);
        Assert.Contains(path.Waypoints, waypoint => waypoint.Action == NavAction.Jump);
    }

    [Fact]
    public void SharpHalfMetreBridgeLipIsAuthoritativelyValidated()
    {
        var platformCentre = new Vector3(
            0.5f,
            SyntheticTerrain.GroundHeight + 0.25f,
            0f);
        var platformExtent = new Vector3(2f, 0.25f, 1.25f);
        var map = SyntheticTerrain.Build((x, y, z) =>
            MathF.Min(
                y - SyntheticTerrain.GroundHeight,
                TerrainEdits.BoxDistance(
                    new Vector3(x, y, z) - platformCentre,
                    platformExtent)));
        var start = CellAt(map, -3, 0);
        var target = CellAt(map, 1, 0);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch);

        Assert.True(path.ReachedGoal);
        // The solver may ground-snap this exact lip or jump it depending on contact interpolation;
        // either result has been simulated with the authoritative capsule before A* accepts it.
        Assert.True(
            path.Waypoints.Any(waypoint => waypoint.Cell.X >= 0),
            "Path never crossed the bridge lip");
    }

    [Fact]
    public void WalkOnlySearchDoesNotJumpAcrossCover()
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

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch with { AllowJump = false });

        Assert.False(path.ReachedGoal);
        Assert.DoesNotContain(path.Waypoints, waypoint => waypoint.Action == NavAction.Jump);
    }

    [Fact]
    public void BlockedSoilFrontierProducesADigActionOnlyWhenEnabled()
    {
        var map = SyntheticTerrain.Wall(0f);
        RepaintSolid(map, BlockType.BlockType_Dirt);
        var start = CellAt(map, -2, 0);
        var unreachable = new NavCell(2, start.Y, 0);

        var ordinary = NavSearch.Find(
            map,
            start,
            new GoalPosition(unreachable),
            CompleteSearch with { AllowDig = false });
        var digging = NavSearch.Find(
            map,
            start,
            new GoalPosition(unreachable),
            CompleteSearch with { AllowDig = true });

        Assert.DoesNotContain(ordinary.Waypoints, waypoint => waypoint.Action == NavAction.Dig);
        Assert.Contains(digging.Waypoints, waypoint => waypoint.Action == NavAction.Dig);
        Assert.Equal(NavAction.Dig, digging.Waypoints[^1].Action);
        Assert.False(digging.ReachedGoal);
    }

    [Fact]
    public void TrenchWithWalkableFloorStillCutsAnExitTowardTheGoal()
    {
        // A trench the actor can walk along but not climb out of. Digging must win here even though
        // ordinary movement is available, or the NPC paces the floor forever: the search makes
        // lateral progress, so a dig gated on "no progress at all" is never chosen.
        const float trenchBottom = 8.5f;
        const float rim = 12.5f;
        const float halfWidth = 1.5f;
        var map = SyntheticTerrain.Build((x, y, z) =>
            y - (MathF.Abs(x) <= halfWidth ? trenchBottom : rim));
        RepaintSolid(map, BlockType.BlockType_Dirt);
        var start = CellAt(map, 0, 0, aroundY: (int)trenchBottom);
        var beyondTheWall = CellAt(map, 8, 0, aroundY: (int)rim);

        var pacing = NavSearch.Find(
            map,
            start,
            new GoalNear(beyondTheWall, 1f),
            CompleteSearch with { AllowDig = false });
        var digging = NavSearch.Find(
            map,
            start,
            new GoalNear(beyondTheWall, 1f),
            CompleteSearch with { AllowDig = true });

        Assert.False(pacing.ReachedGoal);
        Assert.All(
            pacing.Waypoints,
            waypoint => Assert.True(
                waypoint.Position.Y < rim - 1f,
                $"Walk-only route left the trench at {waypoint.Position}"));

        Assert.Contains(digging.Waypoints, waypoint => waypoint.Action == NavAction.Dig);
        Assert.Equal(NavAction.Dig, digging.Waypoints[^1].Action);
        Vector3 bite = digging.Waypoints[^1].Position;
        Assert.True(
            bite.X > 0f,
            $"Expected the cut aimed at the goal side of the trench, got {bite}");
    }

    [Fact]
    public void PacingATrenchEscalatesToADigSearchButABoundedPrefixDoesNot()
    {
        // The gate that decides whether to pay for a second, dig-allowed search. Both cases below
        // return an incomplete path with waypoints, which is why "did the air-only pass come back
        // empty" could not tell them apart and left AllowDig dead for anything but a sealed actor.
        const float trenchBottom = 8.5f;
        const float rim = 12.5f;
        const float halfWidth = 1.5f;
        var trench = SyntheticTerrain.Build((x, y, z) =>
            y - (MathF.Abs(x - z) * 0.70710678f <= halfWidth ? trenchBottom : rim));
        var trenchStart = CellAt(trench, 0, 0, aroundY: (int)trenchBottom);
        var acrossTheWall = CellAt(trench, 10, -10, aroundY: (int)rim);
        var goal = new GoalNear(acrossTheWall, 1f);

        var pacing = NavSearch.Find(
            trench,
            trenchStart,
            goal,
            CompleteSearch with { AllowDig = false });

        Assert.False(pacing.ReachedGoal);
        Assert.NotEmpty(pacing.Waypoints);
        Assert.True(
            NavSearch.NeedsDigEscalation(pacing),
            "A diagonal trench the actor cannot climb out of must escalate to a dig search");

        RepaintSolid(trench, BlockType.BlockType_Dirt);
        var escalated = NavSearch.Find(
            trench,
            trenchStart,
            goal,
            CompleteSearch with { AllowDig = true });
        Assert.Contains(escalated.Waypoints, waypoint => waypoint.Action == NavAction.Dig);

        var open = SyntheticTerrain.Flat();
        var openStart = CellAt(open, -14, 0);
        var openTarget = CellAt(open, 24, 0);
        var openGoal = new GoalPosition(openTarget);
        var prefix = NavSearch.Find(
            open,
            openStart,
            openGoal,
            new NavSearchOptions(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                MaximumExpandedNodes: 60,
                MinimumPartialDistance: 0f));

        Assert.False(prefix.ReachedGoal);
        Assert.NotEmpty(prefix.Waypoints);
        Assert.False(
            NavSearch.NeedsDigEscalation(prefix),
            "A bounded prefix of a good open route must not pay for a second search");
    }

    [Fact]
    public void WalkableRouteIsStillPreferredOverDiggingThroughAWall()
    {
        // The other half of the same decision: a finite wall with open ground around it must not
        // start being tunnelled now that a dig can outrank a partial walk route.
        var wallCentre = new Vector3(0f, SyntheticTerrain.GroundHeight + 5f, 0f);
        var wallExtent = new Vector3(0.6f, 8f, 2.5f);
        var map = SyntheticTerrain.Build((x, y, z) =>
            MathF.Min(
                y - SyntheticTerrain.GroundHeight,
                TerrainEdits.BoxDistance(
                    new Vector3(x, y, z) - wallCentre,
                    wallExtent)));
        RepaintSolid(map, BlockType.BlockType_Dirt);
        var start = CellAt(map, -5, 0);
        var target = CellAt(map, 5, 0);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch with { AllowDig = true });

        Assert.True(path.ReachedGoal);
        Assert.DoesNotContain(path.Waypoints, waypoint => waypoint.Action == NavAction.Dig);
    }

    [Fact]
    public void StoneFrontierNeverProducesADigAction()
    {
        var map = SyntheticTerrain.Wall(0f);
        RepaintSolid(map, BlockType.BlockType_Stone);
        var start = CellAt(map, -2, 0);
        var unreachable = new NavCell(2, start.Y, 0);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(unreachable),
            CompleteSearch with { AllowDig = true });

        Assert.DoesNotContain(path.Waypoints, waypoint => waypoint.Action == NavAction.Dig);
    }

    [Fact]
    public void DirtPitFrontierAimsUpwardToCutAnExit()
    {
        const float pitBottom = 8.5f;
        const float rim = 12.5f;
        var map = SyntheticTerrain.Build((x, y, z) =>
            y - (x < 0 ? pitBottom : rim));
        RepaintSolid(map, BlockType.BlockType_Dirt);
        var start = CellAt(map, -2, 0, aroundY: (int)pitBottom);
        Vector3 feet = NavTraversal.Position(map, start);

        Assert.True(NavTraversal.TryDig(
            map,
            start,
            dx: 1,
            dz: 0,
            out var target,
            out _));
        Assert.True(
            target.Y > feet.Y + 0.5f,
            $"Expected a rising exit bite above feet {feet.Y:0.00}, got {target.Y:0.00}");
    }

    [Fact]
    public void DeepDirtPitFrontierStillAimsUpwardToCutAnExit()
    {
        const float pitBottom = 4.5f;
        const float rim = 24.5f;
        var map = SyntheticTerrain.Build((x, y, z) =>
            y - (x < 0 ? pitBottom : rim));
        RepaintSolid(map, BlockType.BlockType_Dirt);
        var start = CellAt(map, -2, 0, aroundY: (int)pitBottom);
        Vector3 feet = NavTraversal.Position(map, start);

        Assert.True(NavTraversal.TryDig(
            map,
            start,
            dx: 1,
            dz: 0,
            out var target,
            out _));
        Assert.True(
            target.Y > feet.Y + 0.5f,
            $"Expected a rising exit bite above feet {feet.Y:0.00}, got {target.Y:0.00}");
    }

    [Fact]
    public void NarrowDiagonalBridgeUsesAuthoritativeCapsuleValidation()
    {
        const float trenchBottom = 2.5f;
        const float bridgeHeight = 12.5f;
        const float bridgeHalfWidth = 0.65f;
        float inverseSqrtTwo = 1f / MathF.Sqrt(2f);
        var map = SyntheticTerrain.Build((x, y, z) =>
        {
            float bridge = MathF.Max(
                MathF.Abs(x - z) * inverseSqrtTwo - bridgeHalfWidth,
                y - bridgeHeight);
            return MathF.Min(y - trenchBottom, bridge);
        });
        var start = CellAt(map, -3, -3, aroundY: (int)bridgeHeight);
        var target = CellAt(map, 3, 3, aroundY: (int)bridgeHeight);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch);

        Assert.True(path.ReachedGoal);
        Assert.All(
            path.Waypoints,
            waypoint => Assert.True(
                waypoint.Position.Y > bridgeHeight - 1f,
                $"Path left the bridge at {waypoint.Position}"));
    }

    /// <summary>
    /// The map the NPCs actually walk on: a fifteen-metre trench with one narrow bridge, searched
    /// under the budget the GAME uses rather than the two-second one the other bridge cases get.
    ///
    /// A GUARD, NOT A REPRODUCTION — worth being explicit, because it was written while chasing
    /// ISSUES.md #7 and it does not reproduce it. It passes against the code as it was before
    /// VoxelCursor and before MinimumPartialDistance started measuring progress. Whatever puts NPCs
    /// in the trenches on the real map is not captured here.
    ///
    /// What it does pin is worth keeping: no waypoint ever descends into the trench, and repeated
    /// requests converge on the far bank. One 100 ms search does not cross a trench this wide (it
    /// needs about 527 expansions and gets roughly half that), and it does not have to — the
    /// follower re-requests continuously, so what matters is that each answer starts the next one
    /// closer. Asserting a single-shot crossing would test an idealisation the game never performs.
    /// </summary>
    [Fact]
    public void WideTrenchWithABridgeIsCrossedByRepeatedRequestsAndNeverDescendsIntoIt()
    {
        const float trenchHalfWidth = 7.5f;     // a 15 m spherical brush, as the test map is dug
        const float trenchFloor = 2.5f;
        const float bridgeHalfWidth = 1.5f;
        const float ground = SyntheticTerrain.GroundHeight;

        var map = SyntheticTerrain.Build(
            (x, y, z) =>
            {
                float field = y - ground;
                // Subtract the trench, then add the bridge back across it: max for a cut and min
                // for a fill, the same way TerrainEdits composes a real brush.
                float trench = MathF.Max(MathF.Abs(z) - trenchHalfWidth, trenchFloor - y);
                field = MathF.Max(field, -trench);
                float bridge = MathF.Max(MathF.Abs(x) - bridgeHalfWidth, y - ground);
                return MathF.Min(field, bridge);
            },
            chunkRadius: 2);

        // Well off to one side, so the bridge is nowhere near the straight line to the goal.
        var start = CellAt(map, 10, -12, aroundY: (int)ground);
        var target = CellAt(map, 10, 12, aroundY: (int)ground);
        var goal = new GoalPosition(target);
        var cache = new NavTraversalCache();

        var at = start;
        bool arrived = false;
        for (int request = 0; request < 6 && !arrived; request++)
        {
            var path = NavSearch.Find(
                map, at, goal, NavSearchOptions.Default, sharedTraversalCache: cache);

            Assert.All(
                path.Waypoints,
                waypoint => Assert.True(
                    waypoint.Position.Y > ground - 2f,
                    $"Request {request} descended into the trench at {waypoint.Position}"));

            Assert.True(
                path.Waypoints.Count >= 2,
                $"Request {request} from {at} produced no route at all");

            var next = path.Waypoints[^1].Cell;
            Assert.True(next != at, $"Request {request} from {at} made no progress");
            at = next;
            arrived = path.ReachedGoal;
        }

        Assert.True(arrived, $"Never reached the far bank; got as far as {at}");
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
    public void TemporarilyBlockedCellRoutesAroundARepeatedStall()
    {
        var map = SyntheticTerrain.Flat();
        var start = CellAt(map, -3, 0);
        var target = CellAt(map, 3, 0);
        var blocked = CellAt(map, 0, 0);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch,
            blockedCellKey: blocked.Key);

        Assert.True(path.ReachedGoal);
        Assert.DoesNotContain(path.Waypoints, waypoint => waypoint.Cell == blocked);
    }

    [Fact]
    public void PathCorridorIgnoresFarTerrainEditsAndRejectsNearbyOnes()
    {
        var map = SyntheticTerrain.Build(
            (x, y, z) => y - SyntheticTerrain.GroundHeight,
            chunkRadius: 3);
        var start = CellAt(map, -4, 0);
        var target = CellAt(map, 4, 0);
        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch);
        Assert.True(NavPathTerrain.TryStamp(
            map,
            path,
            map.EditVersion,
            out var stamped));

        TerrainEdits.ApplyBox(
            map,
            new Vector3(40f, SyntheticTerrain.GroundHeight, 40f),
            Vector3.One,
            EditMode.Subtract,
            BlockType.BlockType_Air);
        Assert.True(NavPathTerrain.IsValid(map, stamped));

        TerrainEdits.ApplyBox(
            map,
            new Vector3(0f, SyntheticTerrain.GroundHeight, 0f),
            Vector3.One,
            EditMode.Subtract,
            BlockType.BlockType_Air);
        Assert.False(NavPathTerrain.IsValid(map, stamped));
    }

    [Fact]
    public void SmoothedLongPathStillTracksChunksBetweenItsEndpoints()
    {
        var map = SyntheticTerrain.Build(
            (x, y, z) => y - SyntheticTerrain.GroundHeight,
            chunkRadius: 4);
        var start = CellAt(map, -40, 0);
        var target = CellAt(map, 40, 0);
        var sparsePath = new NavPath(
            [
                new NavWaypoint(start, NavTraversal.Position(map, start)),
                new NavWaypoint(target, NavTraversal.Position(map, target)),
            ],
            ReachedGoal: true,
            Cost: 80f,
            ExpandedNodes: 0);
        Assert.True(NavPathTerrain.TryStamp(
            map,
            sparsePath,
            map.EditVersion,
            out var stamped));

        TerrainEdits.ApplyBox(
            map,
            new Vector3(0f, SyntheticTerrain.GroundHeight, 0f),
            Vector3.One,
            EditMode.Subtract,
            BlockType.BlockType_Air);

        Assert.False(NavPathTerrain.IsValid(map, stamped));
    }

    [Fact]
    public void CollinearSmoothingRetainsPeriodicSquadJoinAnchors()
    {
        var waypoints = Enumerable.Range(0, 25)
            .Select(x => new NavWaypoint(
                new NavCell(x, 12, 0),
                new Vector3(x + 0.5f, 12.5f, 0.5f)))
            .ToArray();

        var smoothed = NavPathSmoothing.RemoveCollinearWalks(
            new NavPath(waypoints, true, 24f, 0));

        Assert.True(smoothed.Waypoints.Count > 2);
        for (int i = 1; i < smoothed.Waypoints.Count; i++)
            Assert.True(
                Vector3.Distance(
                    smoothed.Waypoints[i - 1].Position,
                    smoothed.Waypoints[i].Position)
                <= NavPathSmoothing.MaximumWalkSegmentLength);
    }

    [Fact]
    public void SearchCachesRepeatedTraversalQueries()
    {
        var map = SyntheticTerrain.Flat();
        var start = CellAt(map, -8, -8);
        var target = CellAt(map, 8, 8);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch);

        Assert.True(path.ReachedGoal);
        Assert.True(path.CacheHits > 0);
    }

    [Fact]
    public void IndependentSearchesReuseTheSharedTraversalCache()
    {
        var map = SyntheticTerrain.Flat();
        var start = CellAt(map, -8, -8);
        var target = CellAt(map, 8, 8);
        var cache = new NavTraversalCache();

        _ = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch,
            sharedTraversalCache: cache);
        var repeated = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch,
            sharedTraversalCache: cache);

        Assert.True(repeated.ReachedGoal);
        Assert.True(repeated.CacheHits > 0);
    }

    [Fact]
    public void SearchCanBeCancelledAtItsAmortizedCheckBoundary()
    {
        var map = SyntheticTerrain.Build(
            (x, y, z) => y - SyntheticTerrain.GroundHeight,
            chunkRadius: 4);
        var start = CellAt(map, -50, 0);
        var target = CellAt(map, 50, 0);

        var path = NavSearch.Find(
            map,
            start,
            new GoalPosition(target),
            CompleteSearch,
            cancellationRequested: () => true);

        Assert.False(path.ReachedGoal);
        Assert.Empty(path.Waypoints);
        Assert.Equal(64, path.ExpandedNodes);
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

    private static void RepaintSolid(ChunkMap map, BlockType material)
    {
        foreach (var chunk in map.Snapshot())
            for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            {
                var voxel = chunk[i];
                if (voxel.Distance < 0f)
                {
                    voxel.Material = material;
                    chunk[i] = voxel;
                }
            }
    }
}
