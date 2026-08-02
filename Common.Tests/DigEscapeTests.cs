using System.Numerics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// Can an actor at the bottom of a pit dig its way out?
///
/// This drives the same loop MobSystem does — search with digging allowed, apply the one bite the
/// search asked for, re-resolve where the actor is now standing, search again — because that
/// iteration IS the behaviour. A single search only ever returns one bite, so nothing about digging
/// out can be judged from one call.
/// </summary>
public class DigEscapeTests(ITestOutputHelper output)
{
    private const float Ground = SyntheticTerrain.GroundHeight;   // 12.5
    private const float PitFloor = 2.5f;                          // a ten-metre pit
    // 6.5 so the boundary falls between voxel SAMPLES, which sit on the integers: sample 6 is inside
    // the pit and sample 7 is solid wall. On a whole number the boundary passes exactly through a
    // sample, which reads as distance 0 — neither solid nor air — and every test built on it lies.
    private const float PitHalfWidth = 6.5f;

    /// <summary>Flat soil with a square pit in the middle, walls of diggable dirt.</summary>
    private static ChunkMap Pit()
    {
        var map = SyntheticTerrain.Build(
            (x, y, z) =>
            {
                float field = y - Ground;
                float pit = MathF.Max(
                    MathF.Max(MathF.Abs(x) - PitHalfWidth, MathF.Abs(z) - PitHalfWidth),
                    PitFloor - y);
                return MathF.Max(field, -pit);
            },
            chunkRadius: 2);

        // The walls have to be soil: SubtractSoil refuses stone, and a stone-walled pit is
        // inescapable by design rather than by bug.
        foreach (var chunk in map.Snapshot())
            for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            {
                var voxel = chunk[i];
                if (voxel.Distance < 0f) voxel.Material = BlockType.BlockType_Dirt;
                chunk[i] = voxel;
            }

        return map;
    }

    [Fact]
    public void PitFrontierProducesADigPlanWithinTheDefaultBudget()
    {
        var map = Pit();
        Assert.True(
            NavTraversal.TryFindStandable(map, 5, 0, (int)PitFloor, 4, 4, out var at, out _));
        var target = CellAt(map, 20, 0);

        var path = NavSearch.Find(
            map,
            at,
            new GoalNear(target, 1.5f),
            NavSearchOptions.Default with { AllowDig = true, AllowJump = true });

        Assert.Contains(path.Waypoints, waypoint => waypoint.Action == NavAction.Dig);
    }

    [Fact]
    public void ActorAtTheBottomOfAPitDigsItsWayOut()
    {
        var map = Pit();
        var options = NavSearchOptions.Default with { AllowDig = true, AllowJump = true };

        Assert.True(
            NavTraversal.TryFindStandable(map, 0, 0, (int)PitFloor, 4, 4, out var at, out _),
            "no standable cell on the pit floor");
        var target = CellAt(map, 20, 0);
        var goal = new GoalNear(target, 1.5f);

        float startY = at.Y;
        int digs = 0;
        float bestY = startY;

        // MobSystem remembers where it is cutting and feeds it back, which is what stops the search
        // opening a fresh frontier every replan. Without it the actor shaves a long shelf sideways
        // along the wall instead of stacking one staircase.
        NavCell? digSite = null;

        // Generous: at two digs a second this is a few minutes of in-game excavation. A ten-metre
        // pit is meant to be a serious obstacle, not an instant one.
        for (int step = 0; step < 1500; step++)
        {
            // No shared cache: every dig changes the field, and the cache is generation-scoped
            // anyway, so a fresh one per search is what the search would effectively see.
            var path = NavSearch.Find(map, at, goal, options, preferredDigSite: digSite);

            var digWaypoint = path.Waypoints.FirstOrDefault(w => w.Action == NavAction.Dig);
            if (digWaypoint.Action == NavAction.Dig)
            {
                TerrainEdits.ApplyBox(
                    map,
                    digWaypoint.Position,
                    Digging.Bite,
                    EditMode.SubtractSoil,
                    BlockType.BlockType_Air,
                    EditShape.Sphere,
                    Digging.BiteStrength);
                digs++;
                digSite = digWaypoint.Cell;
            }

            // Step up onto whatever the cut just made standable, the way the follower would once
            // the new tread exists. Nearest-first, so it takes the step rather than teleporting.
            if (NavTraversal.TryFindNearestStandable(
                    map,
                    new Vector3(at.X + 0.5f, at.Y + 1.5f, at.Z + 0.5f),
                    horizontalRadius: 1,
                    out var stepped)
                && stepped.Y > at.Y
                && NavTraversal.TryStep(map, at, stepped, out _))
                at = stepped;

            // Walk the route as the follower does: up to the dig frontier, never onto it. The dig
            // waypoint's cell is the material being cut, which is not somewhere to stand.
            for (int i = path.Waypoints.Count - 1; i >= 0; i--)
            {
                var waypoint = path.Waypoints[i];
                if (waypoint.Action == NavAction.Dig) continue;
                if (!NavTraversal.Standable(map, waypoint.Cell.X, waypoint.Cell.Y, waypoint.Cell.Z, out _))
                    continue;
                at = waypoint.Cell;
                break;
            }

            // And re-settle onto whatever the excavation left underfoot.
            if (NavTraversal.TryFindStandable(map, at.X, at.Z, at.Y, 3, 3, out var settled, out _))
                at = settled;

            bestY = MathF.Max(bestY, at.Y);
            if (path.ReachedGoal) break;   // walked first, so the climb is recorded
        }

        output.WriteLine(
            $"start Y={startY}, best Y={bestY}, final {at}, {digs} digs, "
          + $"pit rim is Y={Ground}");

        Assert.True(
            bestY >= Ground - 1f,
            $"Never climbed out: got from Y={startY} to Y={bestY} in {digs} digs, "
          + $"rim is at Y={Ground}");
    }

    /// <summary>
    /// One step, by hand, with the search taken out of it: clear the headroom the staircase rule
    /// says to clear, then ask whether the tread is standable. If this fails the geometry is wrong;
    /// if it passes, anything still stuck is the search or the follower.
    /// </summary>
    [Fact]
    public void ClearingHeadroomAboveATreadMakesItStandable()
    {
        var map = Pit();

        Assert.True(
            NavTraversal.TryFindStandable(map, 5, 0, (int)PitFloor, 4, 4, out var at, out _),
            "no standable cell beside the pit wall");

        const int wallX = 7;   // first solid column: the pit is |x| < 7
        int tread = at.Y + 1;
        output.WriteLine($"standing {at}, cutting the stair at x={wallX}, tread y={tread}");

        for (int bite = 0; bite < 40; bite++)
        {
            if (NavTraversal.Standable(map, wallX, tread, 0, out float surfaceY))
            {
                output.WriteLine($"  tread standable after {bite} bites, surfaceY={surfaceY:0.00}");
                break;
            }

            // The rule under test: clear the lowest not-yet-clear sample above the tread.
            bool cut = false;
            for (int y = tread + 1; y <= tread + 4 && !cut; y++)
            for (int sz = 0; sz <= 1 && !cut; sz++)
            for (int sx = 0; sx <= 1 && !cut; sx++)
            {
                map.TryGetVoxel(wallX + sx, y, sz, out var voxel);
                if (voxel.Distance >= 0.5f) continue;
                TerrainEdits.ApplyBox(
                    map,
                    new Vector3(wallX + sx, y, sz),
                    Digging.Bite,
                    EditMode.SubtractSoil,
                    BlockType.BlockType_Air,
                    EditShape.Sphere,
                    Digging.BiteStrength);
                cut = true;
            }
            if (!cut) { output.WriteLine($"  [{bite}] nothing left to cut"); break; }
        }

        Assert.True(
            NavTraversal.Standable(map, wallX, tread, 0, out _),
            $"tread at ({wallX}, {tread}, 0) never became standable");

        // The tread being standable is not enough: the actor has to be able to REACH it. The last
        // standable floor cell is two cells back, because a vertical wall denies the adjacent cell
        // the capsule clearance it needs, so the cut has to open that cell up too.
        Assert.True(
            NavTraversal.TryStep(map, at, new NavCell(wallX - 1, at.Y, 0), out _)
            || NavTraversal.Standable(map, wallX - 1, at.Y, 0, out _),
            $"the cell between the actor and the stair ({wallX - 1}) is still unreachable");
    }

    private static NavCell CellAt(ChunkMap map, int x, int z)
    {
        Assert.True(
            NavTraversal.TryFindStandable(map, x, z, (int)Ground, 6, 6, out var cell, out _),
            $"no standable cell at ({x}, {z})");
        return cell;
    }
}
