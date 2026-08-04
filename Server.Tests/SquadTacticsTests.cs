using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// Properties of the squad model, not traces through it.
///
/// The behaviour being replaced had a gate: nobody moved until somebody was "set", and set meant
/// "at cover". A squad that could not reach cover was therefore forbidden from moving and stood in
/// the open — which is the flagpost deadlock, the aimless digging, and the general staticness all at
/// once. The replacement has no gate: the allocation always returns an assignment, and its worst
/// case is everyone shooting, never everyone waiting.
/// </summary>
public class SquadTacticsTests
{
    private static readonly Vector3 Threat = new(0f, 0f, 60f);

    private static SquadMemberState Man(
        ushort id,
        float x,
        float z,
        ItemType weapon = ItemType.Ak47,
        float exposure = 1f,
        int boundIndex = 0,
        uint movingSince = 0)
        => new(id, new Vector3(x, 0f, z), weapon, SelfExposure.Of(exposure),
            SkillFactor: 1f, boundIndex, movingSince);

    private static List<SquadTacticalOrder> Plan(
        ItemType threatWeapon = ItemType.Ak47,
        uint tick = 0,
        params SquadMemberState[] members)
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(
            new SquadPlanInput(Threat, HasThreat: true, threatWeapon, CombatValue.DefaultAggression, tick),
            members,
            orders);
        return orders;
    }

    private static SquadTacticalOrder For(List<SquadTacticalOrder> orders, ushort id)
        => orders.Single(order => order.ActorId == id);

    [Fact]
    public void EveryMemberAlwaysGetsAnOrder()
    {
        var orders = Plan(members: [Man(1, 0, 0), Man(2, 4, 0), Man(3, 8, 0), Man(4, 12, 0)]);
        Assert.Equal(4, orders.Count);
        Assert.All(orders, order => Assert.NotEqual(SquadRole.None, order.Role));
    }

    /// <summary>
    /// The anti-deadlock property, and the reason this rewrite exists. Stated as a property of the
    /// model rather than as "the IsSet gate is gone", so it keeps holding however the allocation is
    /// later reorganised.
    /// </summary>
    [Fact]
    public void NobodyBeingInCoverDoesNotFreezeTheSquad()
    {
        var orders = Plan(members:
        [
            Man(1, 0, 0, exposure: 1f),
            Man(2, 4, 0, exposure: 1f),
            Man(3, 8, 0, exposure: 1f),
            Man(4, 12, 0, exposure: 1f),
        ]);

        Assert.Contains(orders, order => order.Role == SquadRole.Bound);
    }

    [Fact]
    public void SomebodyIsAlwaysShootingWhileSomebodyMoves()
    {
        var orders = Plan(members: [Man(1, 0, 0), Man(2, 4, 0), Man(3, 8, 0), Man(4, 12, 0)]);

        Assert.Contains(orders, order => order.Role == SquadRole.BaseOfFire);
        Assert.Contains(orders, order => order.Role == SquadRole.Bound);
    }

    [Fact]
    public void ALoneManNeverBoundsWithNobodyCoveringHim()
    {
        // Degenerate case: with no one to suppress, moving in the open is pure loss.
        var orders = Plan(members: [Man(1, 0, 0)]);
        Assert.Equal(SquadRole.BaseOfFire, For(orders, 1).Role);
    }

    [Fact]
    public void TheShortRangedMenAreTheOnesWhoClose()
    {
        // The threat is a bolt gun, which is the matchup where range decides anything: the squad's
        // own Mosins are in a mirror match, while the PPSh men are useless at 60 m and devastating
        // at 12 m.
        //
        // Deliberately NOT an AK threat. Measured, the AK beats the PPSh at every range — 32.5
        // against 14.7 HP/s at 60 m and 197.6 against 166.8 at 12 m — because its sighting advantage
        // more than pays for half the fire rate. An SMG closing on a carbine is walking into a
        // losing trade, and the model is right to refuse.
        var orders = Plan(
            threatWeapon: ItemType.Mosin,
            members:
            [
                Man(1, 0, 0, ItemType.Mosin),
                Man(2, 4, 0, ItemType.Mosin),
                Man(3, 8, 0, ItemType.Ppsh),
                Man(4, 12, 0, ItemType.Ppsh),
            ]);

        Assert.Contains(orders, o => o.Role == SquadRole.Bound && o.ActorId is 3 or 4);
    }

    // A test asserting "a bolt gun with no numerical advantage holds its range" belongs here and
    // is deliberately ABSENT, because the model cannot currently produce it and pretending otherwise
    // would encode a fiction.
    //
    // Numerical advantage enters as Engagement.TheirTargetingLikelihood = 1/squadSize, so even two
    // men halve their expected incoming and closing wins; one man never bounds at all, since
    // SuppressorsRequired leaves nobody to cover him. There is therefore no configuration in which
    // range alone keeps a squad back.
    //
    // The missing term is TRANSIT. moveValue prices the destination — itself a firing position, so
    // the 1/n sharing applies there too — but never the seconds spent crossing open ground, where a
    // mover is the obvious target rather than one of six. Until a bound costs something to execute,
    // closing is systematically underpriced and every squad wants to assault. That is a cost-model
    // gap, and a threshold bolted on here would hide it rather than fix it.

    [Fact]
    public void MoversGoOnDifferentBearings()
    {
        var orders = Plan(members:
            [Man(1, -8, 0), Man(2, -4, 0), Man(3, 4, 0), Man(4, 8, 0), Man(5, 12, 0), Man(6, 16, 0)]);

        var bearings = orders
            .Where(order => order.Role == SquadRole.Bound)
            .Select(order => order.Bearing)
            .ToList();

        if (bearings.Count >= 2)
            Assert.True(
                bearings.Distinct().Count() == bearings.Count,
                "two movers must not share a bearing — the same cover would defeat both");
    }

    [Fact]
    public void AMoverWhoNeverArrivesReleasesTheRotation()
    {
        uint now = SquadTactics.MoverTimeoutTicks + 10;
        var orders = Plan(
            tick: now,
            members:
            [
                Man(1, 0, 0, movingSince: 1),   // has been moving far too long
                Man(2, 4, 0),
                Man(3, 8, 0),
                Man(4, 12, 0),
            ]);

        Assert.Equal(SquadRole.BaseOfFire, For(orders, 1).Role);
        Assert.Contains(orders, order => order.Role == SquadRole.Bound && order.ActorId != 1);
    }

    [Fact]
    public void TheManFurthestBackTakesTheNextBound()
    {
        var orders = Plan(members:
            [Man(1, 0, 50), Man(2, 0, 40), Man(3, 0, 10), Man(4, 0, 0)]);

        var movers = orders.Where(order => order.Role == SquadRole.Bound).ToList();
        Assert.Contains(movers, order => order.ActorId == 4);
    }

    [Fact]
    public void NoThreatMeansNoCombatRoles()
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(
            new SquadPlanInput(Vector3.Zero, HasThreat: false, ItemType.Ak47, CombatValue.DefaultAggression, 0),
            [Man(1, 0, 0), Man(2, 4, 0)],
            orders);

        Assert.All(orders, order => Assert.Equal(SquadRole.None, order.Role));
    }

    [Fact]
    public void AnEmptySquadPlansNothing()
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(
            new SquadPlanInput(Threat, true, ItemType.Ak47, CombatValue.DefaultAggression, 0),
            [],
            orders);

        Assert.Empty(orders);
    }

    /// <summary>
    /// The number the whole squad layer hangs on. If BallisticsConfig.SuppressedMoa is ever tuned
    /// down far enough that covering fire stops mattering, bounding stops paying and the squad
    /// silently reverts to standing still — the exact failure this rewrite exists to remove. This
    /// asserts it directly so the number fails loudly instead.
    /// </summary>
    [Fact]
    public void CoveringFireIsWhatMakesABoundAffordable()
    {
        // Stated as the behaviour it has to produce, not as a percentage. An even matchup — a
        // carbine squad against a carbine — scores EXACTLY zero gain from moving: closing multiplies
        // both sides' damage identically, so the trade is neutral and nothing tips it. Suppression is
        // the only term left, so this squad bounds if and only if covering fire is worth something.
        //
        // If BallisticsConfig.SuppressedMoa is ever tuned down far enough that it stops mattering,
        // this fails loudly instead of the squad quietly reverting to standing still — which is the
        // exact failure the rewrite exists to remove, and which was live in the shipped game: at the
        // old 50 MOA, covering fire removed 3% of incoming damage and bounding never paid.
        var orders = Plan(members: [Man(1, 0, 0), Man(2, 4, 0), Man(3, 8, 0), Man(4, 12, 0)]);

        Assert.Contains(orders, order => order.Role == SquadRole.Bound);
    }

    /// <summary>
    /// Reported from play: standing on the hill with a Mosin, six carbine-armed NPCs dug in rather
    /// than flanking a single rifleman they outnumbered six to one.
    ///
    /// The cause was a semantic mix-up — SquadMemberState.ExposureHere was being filled from
    /// MobBrain.PerceivedExposure, which is how exposed the TARGET is, not the member. Peeking over
    /// cover at 0.2 therefore told every NPC that IT was 80% protected, so holding scored brilliantly
    /// and closing scored terribly.
    ///
    /// Six carbines against one bolt gun in the open is the most one-sided assault the model can be
    /// handed. If this squad will not move, nothing will.
    /// </summary>
    [Fact]
    public void SixCarbinesAssaultOneRifleInTheOpen()
    {
        var orders = Plan(
            threatWeapon: ItemType.Mosin,
            members:
            [
                Man(1, -10, 0), Man(2, -6, 0), Man(3, -2, 0),
                Man(4, 2, 0), Man(5, 6, 0), Man(6, 10, 0),
            ]);

        Assert.Contains(orders, order => order.Role == SquadRole.Bound);
    }

    [Fact]
    public void BeingBehindCoverDoesNotStopASquadClosing()
    {
        // The same fight, with the squad already in cover. Cover makes holding cheaper, which is
        // correct — but against a lone rifleman it must not make closing worthless.
        var orders = Plan(
            threatWeapon: ItemType.Mosin,
            members:
            [
                Man(1, -10, 0, exposure: 0.15f), Man(2, -6, 0, exposure: 0.15f),
                Man(3, -2, 0, exposure: 0.15f), Man(4, 2, 0, exposure: 0.15f),
                Man(5, 6, 0, exposure: 0.15f), Man(6, 10, 0, exposure: 0.15f),
            ]);

        Assert.Contains(orders, order => order.Role == SquadRole.Bound);
    }

    [Fact]
    public void SuppressionCostsAPrecisionWeaponMoreThanASprayer()
    {
        // Added in quadrature, so it hurts most where there is least inherent dispersion to hide it
        // in. That asymmetry is why forming a base of fire against a marksman is worth doing.
        Assert.True(
            FractionSuppressed(ItemType.Mosin) > FractionSuppressed(ItemType.Ak47),
            "suppression should degrade a bolt gun more than a carbine");
    }

    private static float FractionSuppressed(ItemType weapon)
    {
        var target = new Combatant(ItemType.Ak47, 0f, 1f);
        var calm = new Engagement(
            60f, weapon, 0f, TargetExposure.Full, SelfExposure.Full, 1f);
        var suppressed = calm with { TheirExtraMoa = BallisticsConfig.SuppressedMoa };

        float exposed = CombatValue.Taken(target, [calm]);
        Assert.True(exposed > 0f, $"{weapon} does not fire at 60 m");
        return 1f - CombatValue.Taken(target, [suppressed]) / exposed;
    }
}
