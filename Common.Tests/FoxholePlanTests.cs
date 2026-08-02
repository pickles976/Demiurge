using System.Numerics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// The shape of a dug fighting position. The complaint this answers is "not just a 1x1 hole" — a
/// cut that satisfies a depth check but is too narrow to occupy is not cover.
/// </summary>
public class FoxholePlanTests(ITestOutputHelper output)
{
    private const float Ground = SyntheticTerrain.GroundHeight;

    private static ChunkMap Soil()
    {
        var map = SyntheticTerrain.Flat();
        foreach (var chunk in map.Snapshot())
            for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            {
                var voxel = chunk[i];
                if (voxel.Distance < 0f) voxel.Material = BlockType.BlockType_Dirt;
                chunk[i] = voxel;
            }
        return map;
    }

    /// <summary>Runs the plan to completion, returning every square it cut.</summary>
    private (HashSet<(int X, int Z)> Squares, int Bites, float DeepestFloor) Excavate(
        ChunkMap map,
        Vector3 feet,
        Vector3 toward)
    {
        var squares = new HashSet<(int X, int Z)>();
        int bites = 0;
        while (bites < 400 && FoxholePlan.NextBite(map, feet, toward, Ground) is { } target)
        {
            TerrainEdits.ApplyBox(
                map, target, Digging.Bite, EditMode.SubtractSoil,
                BlockType.BlockType_Air, EditShape.Sphere, Digging.BiteStrength);
            squares.Add(((int)MathF.Round(target.X), (int)MathF.Round(target.Z)));
            bites++;
        }

        float deepest = Ground;
        foreach (var (x, z) in squares)
            if (SurfaceQuery.HighestSurfaceY(map, x, z) is { } y)
                deepest = MathF.Min(deepest, y);

        return (squares, bites, deepest);
    }

    [Fact]
    public void DigsAHoleWideEnoughToOccupyRatherThanAPostHole()
    {
        var map = Soil();
        var feet = new Vector3(0.5f, Ground, 0.5f);
        var toward = Vector3.UnitZ;                 // threat lies toward +Z

        var (squares, bites, deepest) = Excavate(map, feet, toward);

        output.WriteLine(
            $"{bites} bites over {squares.Count} squares, floor down to {deepest:0.00} "
          + $"(grade {Ground}, target {Ground - FoxholePlan.Depth})");
        foreach (var square in squares.OrderBy(s => s.Z).ThenBy(s => s.X))
            output.WriteLine($"  ({square.X}, {square.Z})");

        // The whole point: more than one square, and deep enough to crouch in.
        Assert.True(squares.Count >= 4, $"only cut {squares.Count} square(s) — that is a post-hole");
        // Deep enough to crouch below, and NOT a pit: overlapping spherical bites compound in the
        // middle, so the floor has to be checked at both ends or a "foxhole" becomes a grave.
        Assert.True(
            deepest <= Ground - FoxholePlan.Depth + 0.35f,
            $"floor only reached {deepest:0.00}, wanted about {Ground - FoxholePlan.Depth}");
        Assert.True(
            deepest >= Ground - 2f * FoxholePlan.Depth,
            $"dug a pit rather than a fighting position: floor {deepest:0.00}, grade {Ground}");
    }

    [Fact]
    public void FinishesTheTwoDeepCentreBeforeWidening()
    {
        var map = Soil();
        var feet = new Vector3(0.5f, Ground, 0.5f);
        var toward = Vector3.UnitZ;
        int centreBites = 0;

        while (centreBites < 100
               && FoxholePlan.NextBite(map, feet, toward, Ground) is { } target)
        {
            var square = ((int)MathF.Round(target.X), (int)MathF.Round(target.Z));
            if (square is not ((0 or 1), (0 or 1)))
                break;
            TerrainEdits.ApplyBox(
                map, target, Digging.Bite, EditMode.SubtractSoil,
                BlockType.BlockType_Air, EditShape.Sphere, Digging.BiteStrength);
            centreBites++;
        }

        Assert.True(NavTraversal.TryFindStandable(
            map, 0, 0, (int)Ground, 5, 1, out _, out float centre));
        Assert.True(centreBites > 0);
        Assert.True(
            centre <= Ground - FoxholePlan.Depth + 0.35f,
            $"centre widened early at {centre:0.00}");
    }

    [Fact]
    public void ExpansionCreatesStandingFiringShelvesAtDifferentDepths()
    {
        var map = Soil();
        var feet = new Vector3(0.5f, Ground, 0.5f);

        _ = Excavate(map, feet, Vector3.UnitZ);

        bool centreOk = NavTraversal.TryFindStandable(
            map, 0, 0, (int)Ground, 5, 1, out _, out float centre);
        bool rearLeftOk = NavTraversal.TryFindStandable(
            map, -1, -1, (int)Ground, 5, 1, out _, out float rearLeft);
        bool rearRightOk = NavTraversal.TryFindStandable(
            map, 1, -1, (int)Ground, 5, 1, out _, out float rearRight);
        bool leftOk = NavTraversal.TryFindStandable(
            map, -2, 0, (int)Ground, 5, 1, out _, out float left);
        bool rightOk = NavTraversal.TryFindStandable(
            map, 2, 0, (int)Ground, 5, 1, out _, out float right);
        output.WriteLine(
            $"centre {centreOk} {centre:0.00}; rear L/R {rearLeftOk}/{rearRightOk} "
          + $"{rearLeft:0.00}/{rearRight:0.00}; left {leftOk} {left:0.00}; "
          + $"right {rightOk} {right:0.00}");
        Assert.True(centreOk);
        Assert.True(rearLeftOk);
        Assert.True(rearRightOk);
        Assert.True(leftOk);
        Assert.True(rightOk);
        Assert.True(rearLeft > centre + 0.15f, $"rear {rearLeft:0.00}, centre {centre:0.00}");
        Assert.True(rearRight > centre + 0.15f, $"rear {rearRight:0.00}, centre {centre:0.00}");
        Assert.True(left > centre + 0.35f, $"left {left:0.00}, centre {centre:0.00}");
        Assert.True(right > centre + 0.35f, $"right {right:0.00}, centre {centre:0.00}");
    }

    [Fact]
    public void LeavesTheParapetOnTheThreatSideIntact()
    {
        var map = Soil();
        var feet = new Vector3(0.5f, Ground, 0.5f);
        var toward = Vector3.UnitZ;

        var (squares, _, _) = Excavate(map, feet, toward);

        // Forward of the actor is the lip it shoots over. What matters is that the GROUND there
        // survives, not which voxels the brush clipped: a 0.7 m sphere on an irregular surface
        // always spills a little, so asserting on exact cut voxels tests the brush, not the plan.
        float parapet = SurfaceQuery.HighestSurfaceY(map, 0, 2) ?? 0f;
        Assert.True(
            parapet >= Ground - 0.25f,
            $"the parapet in front was cut away: surface {parapet:0.00}, grade {Ground}");
    }

    [Fact]
    public void StopsOnceThePositionIsDugRatherThanBuryingItself()
    {
        var map = Soil();
        var feet = new Vector3(0.5f, Ground, 0.5f);
        var toward = Vector3.UnitZ;

        var (_, bites, _) = Excavate(map, feet, toward);

        Assert.True(bites < 400, "never terminated");
        Assert.Null(FoxholePlan.NextBite(map, feet, toward, Ground));
    }
}
