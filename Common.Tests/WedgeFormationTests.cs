using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// The travelling shape of a squad. Pure geometry, so it is worth pinning: the failure it replaces
/// was a whole squad walking to one point in a clump, which is one grenade for four men.
/// </summary>
public class WedgeFormationTests
{
    private static readonly Vector3 From = new(0f, 0f, 0f);
    private static readonly Vector3 Objective = new(0f, 0f, 100f);   // due north of the squad

    [Fact]
    public void PointManTakesTheObjectiveItself()
        => Assert.Equal(Objective, WedgeFormation.Slot(Objective, From, 0));

    [Fact]
    public void FlanksAlternateSidesAndFallBackByRank()
    {
        var right = WedgeFormation.Slot(Objective, From, 1);
        var left = WedgeFormation.Slot(Objective, From, 2);
        var farRight = WedgeFormation.Slot(Objective, From, 3);

        // Approach runs along +Z, so the lateral axis is X and the two flanks straddle it.
        Assert.True(right.X > 0f, $"slot 1 should be right of the axis, got {right}");
        Assert.True(left.X < 0f, $"slot 2 should be left of the axis, got {left}");
        Assert.Equal(right.X, -left.X, 3);

        // Every man off the point is BEHIND it, and the outer files further back than the inner.
        Assert.True(right.Z < Objective.Z, "slot 1 should trail the point man");
        Assert.True(MathF.Abs(farRight.X) > MathF.Abs(right.X), "rank 2 should be wider than rank 1");
        Assert.True(farRight.Z < right.Z, "rank 2 should trail rank 1");
    }

    [Fact]
    public void FormationTurnsWithTheApproachRatherThanStayingWorldAligned()
    {
        // Same objective, approached from the east instead of the south. The flanks must rotate
        // with the axis of advance — a V that stays world-aligned turns into a file the moment the
        // squad approaches along the wrong bearing, which is exactly what it exists to avoid.
        var fromSouth = WedgeFormation.Slot(Objective, From, 1);
        var fromEast = WedgeFormation.Slot(Objective, Objective + new Vector3(100f, 0f, 0f), 1);

        Assert.True(
            Vector3.Distance(fromSouth, fromEast) > WedgeFormation.Spacing,
            $"slot 1 barely moved when the approach changed: {fromSouth} vs {fromEast}");

        // Still the same distance off the objective, just on a different bearing.
        Assert.Equal(
            Vector3.Distance(Objective, fromSouth),
            Vector3.Distance(Objective, fromEast),
            3);
    }

    [Fact]
    public void SquadStandingOnItsObjectiveStillGetsDistinctSlots()
    {
        // Degenerate approach: the direction is undefined, but four men must not be handed the same
        // spot or they pile up on the flag.
        var slots = Enumerable.Range(0, 4)
            .Select(slot => WedgeFormation.Slot(Objective, Objective, slot))
            .ToArray();

        Assert.Equal(slots.Length, slots.Distinct().Count());
    }

    [Fact]
    public void MenAreSpacedFarEnoughApartToNotShareOneBurst()
    {
        var slots = Enumerable.Range(0, SquadBlackboardLimits.MaximumMembers)
            .Select(slot => WedgeFormation.Slot(Objective, From, slot))
            .ToArray();

        for (int a = 0; a < slots.Length; a++)
            for (int b = a + 1; b < slots.Length; b++)
                Assert.True(
                    Vector3.Distance(slots[a], slots[b]) >= WedgeFormation.Depth,
                    $"slots {a} and {b} are {Vector3.Distance(slots[a], slots[b]):0.0} m apart");
    }

    /// <summary>Mirrors SquadBlackboard.MaximumMembers, which lives in the server assembly.</summary>
    private static class SquadBlackboardLimits
    {
        public const int MaximumMembers = 4;
    }
}
