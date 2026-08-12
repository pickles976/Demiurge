using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the strategic cost model. Not a trace through the allocator: each of these is a
/// statement about what a flag is WORTH that must survive any allocator written over it.
/// </summary>
public class StrategicValueTests
{
    private const int Us = 1;
    private const int Them = 2;

    private static StrategicFlag Flag(
        int owner,
        int friendly = 0,
        int enemy = 0,
        uint id = 1,
        int approaching = 0,
        float enemySeconds = float.PositiveInfinity)
        => new(
            id,
            Vector3.Zero,
            owner,
            FlagConfig.NeutralTeam,
            0f,
            friendly,
            enemy,
            approaching,
            enemySeconds);

    /// <summary>
    /// The bug, as a property. Taking an undefended flag flips a whole flag of bleed; adding a
    /// second squad to a flag the first already secures flips nothing. Any model where the second
    /// outranks the first produces the mass loop that started this.
    /// </summary>
    [Fact]
    public void ASecondSquadOnASecuredFlagIsWorthLessThanAFirstOnAFreeOne()
    {
        float reinforce = StrategicValue.Marginal(
            Us, Flag(Us, friendly: 4), squadsAlreadyAssigned: 1, travelSeconds: 5f);
        float takeFree = StrategicValue.Marginal(
            Us, Flag(Them), squadsAlreadyAssigned: 0, travelSeconds: 30f);

        Assert.True(
            takeFree > reinforce,
            $"free ground {takeFree} must outbid reinforcement {reinforce}");
    }

    /// <summary>An enemy flag moves the differential by two — they lose one and we gain one — while
    /// a neutral one moves it by one. Derived from ConquestConfig.BleedFor, not picked.</summary>
    [Fact]
    public void TakingAnEnemyFlagIsWorthTwiceTakingANeutralOne()
        => Assert.Equal(
            2f * StrategicValue.Swing(Us, Flag(FlagConfig.NeutralTeam)),
            StrategicValue.Swing(Us, Flag(Them)),
            4);

    /// <summary>Value falls off with how long it takes to start paying, and never goes negative —
    /// a distant flag is worth less, not worth avoiding.</summary>
    [Fact]
    public void ValueDecaysWithTravelAndStaysPositive()
    {
        float near = StrategicValue.Marginal(Us, Flag(Them), 0, travelSeconds: 5f);
        float far = StrategicValue.Marginal(Us, Flag(Them), 0, travelSeconds: 120f);

        Assert.True(near > far);
        Assert.True(far > 0f, "a far flag is worth less, not worth avoiding");
    }

    /// <summary>
    /// Continuity is what kills the oscillation. One man stepping into a capture radius must move
    /// the value a little, never reclassify the flag — a step function over a noisy input is a
    /// reassignment generator.
    /// </summary>
    [Fact]
    public void ValueIsContinuousInPresence()
    {
        float previous = StrategicValue.Marginal(Us, Flag(Them, enemy: 0), 0, 20f);
        for (int enemy = 1; enemy <= 8; enemy++)
        {
            float next = StrategicValue.Marginal(Us, Flag(Them, enemy: enemy), 0, 20f);
            Assert.True(
                MathF.Abs(next - previous) < StrategicValue.TicketsPerSecondPerFlag,
                $"presence {enemy} jumped the value by {MathF.Abs(next - previous)}");
            previous = next;
        }
    }

    /// <summary>Defending something about to be lost is worth as much as taking it back, because it
    /// is the same two-flag swing.</summary>
    [Fact]
    public void HoldingAThreatenedFlagIsWorthAsMuchAsRetakingIt()
        => Assert.Equal(
            StrategicValue.Swing(Us, Flag(Them, enemy: 3)),
            StrategicValue.Swing(Us, Flag(Us, friendly: 1, enemy: 3)),
            4);

    /// <summary>A quiet rear flag nobody is threatening swings nothing, so it cannot pull a squad off
    /// a real objective just for being close.</summary>
    [Fact]
    public void AQuietFriendlyFlagIsWorthNothing()
        => Assert.Equal(0f, StrategicValue.Marginal(Us, Flag(Us), 0, travelSeconds: 1f), 4);

    /// <summary>
    /// Defence is a function of WHEN, not of whether. A flag of ours the enemy is walking up to is
    /// worth defending; the same flag with the enemy on the far side of the map is not. Nothing here
    /// classifies a flag as threatened — the approach time is a continuous input to the same discount
    /// every other flag pays, which is what stops one man crossing an arbitrary line from reshuffling
    /// the whole plan.
    /// </summary>
    [Fact]
    public void OwnedFlagValueRisesSmoothlyAsTheEnemyClosesOnIt()
    {
        float previous = 0f;
        foreach (float seconds in new[] { 240f, 120f, 60f, 30f, 15f, 5f })
        {
            float value = StrategicValue.Marginal(
                Us, Flag(Us, enemySeconds: seconds), 0, travelSeconds: 10f);
            Assert.True(
                value > previous,
                $"an enemy {seconds}s away must be worth more than one further off");
            Assert.True(
                value - previous < StrategicValue.TicketsPerSecondPerFlag,
                $"and must not step: {previous} -> {value}");
            previous = value;
        }

        Assert.True(
            previous
                < StrategicValue.Marginal(Us, Flag(Them), 0, travelSeconds: 10f) * 1.5f,
            "defending must stay comparable to attacking, not dominate it");
    }

    /// <summary>
    /// The same property as <see cref="AQuietFriendlyFlagIsWorthNothing"/>, at the scale the game is
    /// actually played at rather than at the degenerate point where the last enemy is dead.
    ///
    /// The conquest map's flags sit 140-450 m apart and its spawns 95-650 m from them, so "the enemy
    /// is nowhere near this flag" means an approach of 100-160 s, not infinity. A flag nobody can
    /// reach until long after the planning horizon cannot be lost inside the plan, so garrisoning it
    /// must lose to taking ground that is free right now.
    /// </summary>
    [Fact]
    public void AFlagTheEnemyCannotReachWithinTheHorizonLosesToFreeGround()
    {
        // 400 m of walking for them, 300 m for us, i.e. one flag away on the real map.
        float garrison = StrategicValue.Marginal(
            Us, Flag(Us, enemySeconds: 100f), 0, travelSeconds: 75f);
        float freeGround = StrategicValue.Marginal(
            Us, Flag(FlagConfig.NeutralTeam, id: 2), 0, travelSeconds: 75f);

        Assert.True(
            freeGround > garrison,
            $"free ground {freeGround} must outbid a garrison nobody is threatening {garrison}");
    }

    /// <summary>The other side of it, so the correction above cannot be bought by making defence
    /// worthless: a threat inside the horizon still outranks the same free ground.</summary>
    [Fact]
    public void AFlagUnderImminentThreatStillOutbidsFreeGround()
    {
        float defend = StrategicValue.Marginal(
            Us, Flag(Us, enemySeconds: 15f), 0, travelSeconds: 20f);
        float freeGround = StrategicValue.Marginal(
            Us, Flag(FlagConfig.NeutralTeam, id: 2), 0, travelSeconds: 20f);

        Assert.True(
            defend > freeGround,
            $"a flag about to be taken {defend} must outbid free ground {freeGround}");
    }

    /// <summary>
    /// The other half of the pile-up, and the half a per-plan counter could never see. Every plan
    /// starts with nothing assigned, so a flag six of our men were standing on looked exactly as free
    /// as an empty one to the next squad. Men present and squads assigned are the same thing to the
    /// flag and are counted as one.
    /// </summary>
    [Fact]
    public void MenAlreadyThereDiscountTheFlagForTheNextSquad()
    {
        float covered = StrategicValue.Marginal(
            Us, Flag(Them, approaching: 8), squadsAlreadyAssigned: 0, travelSeconds: 20f);
        float free = StrategicValue.Marginal(
            Us, Flag(Them, approaching: 0, id: 2), squadsAlreadyAssigned: 0, travelSeconds: 20f);

        Assert.True(free > covered * 2f, $"free {free} must clearly outbid covered {covered}");
    }
}
