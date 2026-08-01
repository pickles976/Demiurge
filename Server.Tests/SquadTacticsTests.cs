using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class SquadTacticsTests
{
    private static readonly Vector3 Threat = new(0f, 0f, 60f);

    private static SquadTacticalInput Man(
        ushort id,
        float x,
        float z,
        bool set = false,
        FlankSide side = FlankSide.None,
        int boundIndex = 0,
        bool assault = false,
        bool threatReloading = false,
        bool committed = false)
        => new(
            id,
            new Vector3(x, 0f, z),
            side,
            boundIndex,
            set,
            assault,
            threatReloading,
            committed);

    private static List<SquadTacticalOrder> Plan(params SquadTacticalInput[] members)
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(Threat, hasThreat: true, members, orders);
        return orders;
    }

    private static SquadTacticalOrder For(List<SquadTacticalOrder> orders, ushort id)
        => orders.Single(order => order.ActorId == id);

    [Fact]
    public void NoThreatLeavesEveryoneOnTheirObjective()
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(
            Vector3.Zero,
            hasThreat: false,
            [Man(60000, 0f, 0f), Man(60001, 2f, 0f)],
            orders);

        Assert.All(orders, order => Assert.Equal(SquadRole.None, order.Role));
    }

    [Fact]
    public void NobodyMovesUntilSomeoneIsProvidingFire()
    {
        // The opening beat of the scenario: contact is made, nobody is set, so the whole squad goes to
        // ground and digs in rather than walking into fire.
        var orders = Plan(
            Man(60000, 0f, 0f),
            Man(60001, 2f, 0f),
            Man(60002, 4f, 0f));

        Assert.All(orders, order => Assert.Equal(SquadRole.BaseOfFire, order.Role));
    }

    [Fact]
    public void OnceSomeoneIsSetTheRearManBounds()
    {
        // 60001 is set and closest; 60002 is furthest back, so he is the one who moves up.
        var orders = Plan(
            Man(60001, 0f, 30f, set: true, side: FlankSide.Left),
            Man(60002, 0f, 0f, side: FlankSide.Left));

        Assert.Equal(SquadRole.BaseOfFire, For(orders, 60001).Role);
        Assert.Equal(SquadRole.Bound, For(orders, 60002).Role);
    }

    [Fact]
    public void SquadSplitsAcrossBothSidesOfTheThreatAxis()
    {
        var orders = Plan(
            Man(60000, 0f, 0f, set: true),
            Man(60001, 2f, 0f),
            Man(60002, 4f, 0f),
            Man(60003, 6f, 0f));

        Assert.Contains(orders, order => order.Side == FlankSide.Left);
        Assert.Contains(orders, order => order.Side == FlankSide.Right);
    }

    [Fact]
    public void AssignedSidesAreStickyAcrossReplans()
    {
        // A replan mid-manoeuvre must not send a committed flanker back across the axis, which would
        // walk him through the beaten zone he was avoiding.
        var orders = Plan(
            Man(60000, 0f, 0f, set: true, side: FlankSide.Right),
            Man(60001, 2f, 0f, side: FlankSide.Right),
            Man(60002, 4f, 0f, side: FlankSide.Right));

        Assert.All(orders, order => Assert.Equal(FlankSide.Right, order.Side));
    }

    [Fact]
    public void OnlyOneManPerSideMovesAtATime()
    {
        var orders = Plan(
            Man(60000, 0f, 20f, set: true, side: FlankSide.Left),
            Man(60001, 0f, 10f, side: FlankSide.Left),
            Man(60002, 0f, 0f, side: FlankSide.Left));

        Assert.Equal(
            SquadTactics.MoversPerSide,
            orders.Count(order => order.Role == SquadRole.Bound));
    }

    [Fact]
    public void LeapfrogAlternatesWithoutAnyHandoffState()
    {
        // Emergent rather than a state machine: whoever is furthest from the threat bounds, so once he
        // has moved past his partner the partner is furthest and takes the next bound.
        var first = Plan(
            Man(60001, 0f, 30f, set: true, side: FlankSide.Left),
            Man(60002, 0f, 0f, set: true, side: FlankSide.Left));
        Assert.Equal(SquadRole.Bound, For(first, 60002).Role);

        // 60002 has now bounded forward past 60001.
        var second = Plan(
            Man(60001, 0f, 30f, set: true, side: FlankSide.Left),
            Man(60002, 0f, 40f, set: true, side: FlankSide.Left, boundIndex: 1));
        Assert.Equal(SquadRole.Bound, For(second, 60001).Role);
        Assert.Equal(SquadRole.BaseOfFire, For(second, 60002).Role);
    }

    [Fact]
    public void ALoneSetManDoesNotAbandonOverwatchToBound()
    {
        // One man on his side, nobody set anywhere else: if he moved, nothing would be shooting.
        var orders = Plan(Man(60000, 0f, 0f, set: true, side: FlankSide.Left));

        Assert.Equal(SquadRole.BaseOfFire, For(orders, 60000).Role);
    }

    [Fact]
    public void BoundDestinationsAreOffToTheirOwnSideOfTheAxis()
    {
        var orders = Plan(
            Man(60000, 0f, 0f, set: true, side: FlankSide.Left),
            Man(60001, -1f, 0f, side: FlankSide.Left),
            Man(60002, 1f, 0f, side: FlankSide.Right));

        var left = For(orders, 60001);
        var right = For(orders, 60002);
        Assert.Equal(SquadRole.Bound, left.Role);
        Assert.Equal(SquadRole.Bound, right.Role);
        // Threat is at +Z, so the axis is +Z and the lateral separation shows up on X.
        Assert.True(
            left.Destination.X < 0f && right.Destination.X > 0f,
            $"Expected opposing bearings, got left {left.Destination} right {right.Destination}");
        Assert.True(
            MathF.Abs(left.Destination.X - right.Destination.X) > 10f,
            "Envelope bearings should be far enough apart to split the defender's arc");
    }

    [Fact]
    public void EachBoundClosesTheDistanceAndNarrowsTheEnvelope()
    {
        Vector3 axis = Vector3.UnitZ;
        Vector3 lateral = SquadTactics.LateralAxis(axis);

        Vector3 opening = SquadTactics.EnvelopePosition(
            Threat, axis, lateral, FlankSide.Right, boundIndex: 0);
        Vector3 second = SquadTactics.EnvelopePosition(
            Threat, axis, lateral, FlankSide.Right, boundIndex: 1);
        Vector3 late = SquadTactics.EnvelopePosition(
            Threat, axis, lateral, FlankSide.Right, boundIndex: 8);

        float openingRange = Vector3.Distance(opening, Threat);
        float secondRange = Vector3.Distance(second, Threat);
        float lateRange = Vector3.Distance(late, Threat);

        Assert.True(secondRange < openingRange, "A bound must gain ground");
        Assert.True(lateRange < secondRange, "Successive bounds must keep closing");
        Assert.True(
            lateRange >= SquadTactics.MinimumStandoff - 0.01f,
            "Bounds must not converge onto the threat's own position");
        Assert.True(
            MathF.Abs(late.X) < MathF.Abs(opening.X),
            "The envelope should tighten as the squad closes rather than walking past");
    }

    [Fact]
    public void OrdersCoverEveryMemberExactlyOnce()
    {
        var orders = Plan(
            Man(60003, 6f, 0f),
            Man(60000, 0f, 0f, set: true),
            Man(60002, 4f, 0f),
            Man(60001, 2f, 0f));

        Assert.Equal(4, orders.Count);
        Assert.Equal(4, orders.Select(order => order.ActorId).Distinct().Count());
    }

    [Fact]
    public void AssaultElementWaitsInCoverUntilTheThreatReloads()
    {
        var orders = Plan(
            Man(60000, 0f, 20f, set: true, side: FlankSide.Left, assault: true),
            Man(60001, 2f, 20f, set: true, side: FlankSide.Right, assault: true),
            Man(60002, 0f, 10f, set: true, side: FlankSide.Left),
            Man(60003, 2f, 10f, set: true, side: FlankSide.Right));

        Assert.All(orders, order => Assert.Equal(SquadRole.BaseOfFire, order.Role));
    }

    [Fact]
    public void ReloadTellSendsAssaultElementWhileRiflesKeepFiring()
    {
        var orders = Plan(
            Man(60000, 0f, 20f, set: true, side: FlankSide.Left,
                assault: true, threatReloading: true),
            Man(60001, 2f, 20f, set: true, side: FlankSide.Right,
                assault: true, threatReloading: true),
            Man(60002, 0f, 10f, set: true, side: FlankSide.Left),
            Man(60003, 2f, 10f, set: true, side: FlankSide.Right));

        Assert.Equal(SquadRole.Bound, For(orders, 60000).Role);
        Assert.Equal(SquadRole.Bound, For(orders, 60001).Role);
        Assert.Equal(SquadRole.BaseOfFire, For(orders, 60002).Role);
        Assert.Equal(SquadRole.BaseOfFire, For(orders, 60003).Role);
    }

    [Fact]
    public void AssaultDashFinishesAfterReloadTellEnds()
    {
        var orders = Plan(
            Man(60000, 0f, 20f, side: FlankSide.Left,
                assault: true, committed: true),
            Man(60002, 0f, 10f, set: true, side: FlankSide.Left));

        Assert.Equal(SquadRole.Bound, For(orders, 60000).Role);
        Assert.Equal(SquadRole.BaseOfFire, For(orders, 60002).Role);
    }
}
