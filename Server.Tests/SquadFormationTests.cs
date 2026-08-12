using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class SquadFormationTests
{
    private static readonly Dictionary<ushort, int> Assignments = new();

    private static Dictionary<ushort, int> Plan(params SquadMember[] members)
    {
        var assignments = new Dictionary<ushort, int>();
        SquadFormation.Plan(members, assignments);
        return assignments;
    }

    private static SquadMember At(ushort id, int squad, float x, float z)
        => new(id, squad, new Vector3(x, 0f, z));

    [Fact]
    public void CohesiveSquadKeepsItsMembership()
    {
        var assignments = Plan(
            At(60000, 0, 0f, 0f),
            At(60001, 0, 2f, 0f),
            At(60002, 0, 0f, 2f),
            At(60003, 0, 2f, 2f));

        Assert.All(assignments.Values, squad => Assert.Equal(0, squad));
    }

    [Fact]
    public void SeparatedMemberJoinsTheNearbySquadItIsFightingWith()
    {
        // The bug this exists for: a lone unit adjacent to a squad used to keep its own index forever,
        // and the commander then sent that squad of one to its own objective.
        var assignments = Plan(
            At(60000, 0, 0f, 0f),
            At(60001, 0, 2f, 0f),
            At(60002, 1, 4f, 0f));

        Assert.Equal(assignments[60000], assignments[60002]);
    }

    [Fact]
    public void LoneUnitFarFromEveryoneKeepsItsOwnSquad()
    {
        var assignments = Plan(
            At(60000, 0, 0f, 0f),
            At(60001, 0, 2f, 0f),
            At(60002, 1, 500f, 500f));

        Assert.NotEqual(assignments[60000], assignments[60002]);
    }

    [Fact]
    public void OverStrengthSquadShedsItsFarthestMembers()
    {
        // One man more than a squad holds. The outlier is the one that leaves, not whichever the
        // dictionary happened to yield first.
        var assignments = Plan(
            At(60000, 0, 0f, 0f),
            At(60001, 0, 1f, 0f),
            At(60002, 0, 0f, 1f),
            At(60003, 0, 1f, 1f),
            At(60004, 0, 2f, 0f),
            At(60005, 0, 0f, 2f),
            At(60006, 0, 25f, 25f));

        int core = assignments[60000];
        Assert.Equal(core, assignments[60001]);
        Assert.Equal(core, assignments[60002]);
        Assert.Equal(core, assignments[60003]);
        Assert.Equal(core, assignments[60004]);
        Assert.Equal(core, assignments[60005]);
        Assert.NotEqual(core, assignments[60006]);
        Assert.Equal(
            SquadBlackboard.MaximumMembers,
            assignments.Values.Count(squad => squad == core));
    }

    [Fact]
    public void NoSquadEverExceedsCapacity()
    {
        var members = new SquadMember[16];
        for (int i = 0; i < members.Length; i++)
            members[i] = At((ushort)(60000 + i), 0, i * 0.5f, 0f);

        var assignments = new Dictionary<ushort, int>();
        SquadFormation.Plan(members, assignments);

        foreach (var group in assignments.GroupBy(pair => pair.Value))
            Assert.True(
                group.Count() <= SquadBlackboard.MaximumMembers,
                $"Squad {group.Key} has {group.Count()} members");
    }

    [Fact]
    public void PlanIsDeterministicRegardlessOfInputOrder()
    {
        SquadMember[] forward =
        [
            At(60000, 0, 0f, 0f),
            At(60001, 1, 3f, 0f),
            At(60002, 2, 6f, 0f),
        ];
        var reversed = forward.Reverse().ToArray();

        var first = new Dictionary<ushort, int>();
        var second = new Dictionary<ushort, int>();
        SquadFormation.Plan(forward, first);
        SquadFormation.Plan(reversed, second);

        Assert.Equal(first.OrderBy(pair => pair.Key), second.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void FlankingSpreadDoesNotDissolveTheSquad()
    {
        // Squads legitimately spread out to envelope. Re-forming them mid-manoeuvre would throw away
        // the plan that spread them, so cohesion is deliberately generous.
        var assignments = Plan(
            At(60000, 0, 0f, 0f),
            At(60001, 0, 0f, 18f),
            At(60002, 0, 0f, -18f));

        Assert.All(assignments.Values, squad => Assert.Equal(0, squad));
    }

    [Fact]
    public void EmptyInputProducesNoAssignments()
    {
        Assignments.Clear();
        Assignments[1] = 7;
        SquadFormation.Plan([], Assignments);
        Assert.Empty(Assignments);
    }

    [Fact]
    public void LowestLiveActorIdLeadsRegardlessOfRosterOrder()
    {
        var squad = new SquadBlackboard();
        squad.SetRoster([60004, 60001, 60003], Vector3.Zero);

        Assert.Equal((ushort)60001, squad.LeaderId);

        squad.SetRoster([60004, 60003], Vector3.Zero);
        Assert.Equal((ushort)60003, squad.LeaderId);
    }
}
