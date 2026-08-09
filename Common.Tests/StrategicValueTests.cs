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
        uint id = 1)
        => new(id, Vector3.Zero, owner, FlagConfig.NeutralTeam, 0f, friendly, enemy);

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
}
