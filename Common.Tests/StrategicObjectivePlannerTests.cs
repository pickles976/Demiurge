using System.Numerics;

namespace Demiurge.Tests;

public class StrategicObjectivePlannerTests
{
    [Fact]
    public void LoneIncompleteSquadStillReceivesAnObjective()
    {
        var assignment = Assert.Single(StrategicObjectivePlanner.Plan(
            team: 1,
            squads: [new StrategicSquad(7, Vector3.Zero)],
            flags: [NeutralFlag(10, 5f)]));

        Assert.Equal(7, assignment.SquadId);
        Assert.Equal(10u, assignment.FlagId);
    }

    [Fact]
    public void NeutralOpeningDistributesSquadsAcrossDistinctFlags()
    {
        var squads = new[]
        {
            new StrategicSquad(0, new Vector3(-30f, 0f, 0f)),
            new StrategicSquad(1, new Vector3(30f, 0f, 0f)),
        };
        var flags = new[]
        {
            NeutralFlag(10, -25f),
            NeutralFlag(20, 25f),
        };

        var assignments = StrategicObjectivePlanner.Plan(1, squads, flags);

        Assert.Equal(10u, AssignmentFor(assignments, 0));
        Assert.Equal(20u, AssignmentFor(assignments, 1));
        Assert.Equal(2, assignments.Select(value => value.FlagId).Distinct().Count());
    }

    [Fact]
    public void ThreatenedFriendlyFlagReceivesReinforcementBeforeQuietRearFlag()
    {
        var squads = new[]
        {
            new StrategicSquad(0, new Vector3(-30f, 0f, 0f)),
            new StrategicSquad(1, Vector3.Zero),
            new StrategicSquad(2, new Vector3(30f, 0f, 0f)),
        };
        var flags = new[]
        {
            new StrategicFlag(
                10,
                new Vector3(-20f, 0f, 0f),
                OwnerTeam: 1,
                CapturingTeam: 2,
                Progress: 0.5f,
                FriendlyPresence: 0,
                EnemyPresence: 1),
            NeutralFlag(20, 20f),
            new StrategicFlag(
                30,
                Vector3.Zero,
                OwnerTeam: 1,
                CapturingTeam: 0,
                Progress: 1f,
                FriendlyPresence: 0,
                EnemyPresence: 0),
        };

        var assignments = StrategicObjectivePlanner.Plan(1, squads, flags);

        Assert.True(
            assignments.Any(value => value.FlagId == 10),
            "the attack under way must keep a squad");
        Assert.True(
            assignments.Any(value => value.FlagId == 20),
            "and the free flag next door must not be ignored while two squads share one fight");
        Assert.Single(assignments, value => value.FlagId == 20);
        Assert.DoesNotContain(assignments, value => value.FlagId == 30);
    }

    [Fact]
    /// <summary>
    /// Rewritten 2026-08-09. This used to assert that BOTH squads went to the contested flag while a
    /// free neutral one sat a metre away — which is the reported "the AI only ever fights over one
    /// or two flags and never takes free ground" behaviour, written down as a requirement. It was a
    /// trace through the old priority ladder, whose second slot at a contested flag (800) outranked
    /// the first slot at an empty one (600).
    ///
    /// What survives is the part that is a property rather than a ladder: an attack already under
    /// way is not abandoned. Whether the SECOND squad reinforces it or takes free ground is a
    /// question about marginal value, and StrategicValueTests pins the answer.
    /// </summary>
    public void ActiveAttackKeepsASquadRatherThanBeingAbandoned()
    {
        var squads = new[]
        {
            new StrategicSquad(0, Vector3.Zero),
            new StrategicSquad(1, Vector3.UnitX),
        };
        var flags = new[]
        {
            new StrategicFlag(
                10,
                new Vector3(100f, 0f, 0f),
                OwnerTeam: 2,
                CapturingTeam: 0,
                Progress: 1f,
                FriendlyPresence: 1,
                EnemyPresence: 1),
            NeutralFlag(20, 1f),
        };

        var assignments = StrategicObjectivePlanner.Plan(1, squads, flags);

        Assert.True(
            assignments.Any(value => value.FlagId == 10),
            "the attack under way must keep a squad");
        Assert.True(
            assignments.Any(value => value.FlagId == 20),
            "and the free flag next door must not be ignored while two squads share one fight");
    }

    [Fact]
    public void ExistingAssignmentWinsAnOtherwiseEqualTravelTie()
    {
        var squads = new[]
        {
            new StrategicSquad(0, Vector3.Zero, CurrentFlagId: 20),
            new StrategicSquad(1, Vector3.Zero),
        };
        var flags = new[]
        {
            NeutralFlag(10, -5f),
            NeutralFlag(20, 5f),
        };

        var assignments = StrategicObjectivePlanner.Plan(1, squads, flags);

        Assert.Equal(20u, AssignmentFor(assignments, 0));
        Assert.Equal(10u, AssignmentFor(assignments, 1));
    }

    private static StrategicFlag NeutralFlag(uint id, float x)
        => new(
            id,
            new Vector3(x, 0f, 0f),
            OwnerTeam: 0,
            CapturingTeam: 0,
            Progress: 0f,
            FriendlyPresence: 0,
            EnemyPresence: 0);

    private static uint AssignmentFor(
        IReadOnlyList<StrategicAssignment> assignments,
        int squadId)
        => Assert.Single(assignments, value => value.SquadId == squadId).FlagId;
}
