namespace Demiurge.Tests;

/// <summary>
/// Whether a threat buys an objective off a squad. Both sides in tickets per second, which is the
/// only reason the question has an answer rather than a range constant.
/// </summary>
public class ThreatResponseTests
{
    private static Combatant Self(ItemType weapon) => new(weapon, 0f, 1f);

    private static Engagement At(float range, ItemType theirWeapon)
        => new(range, theirWeapon, 0f, TargetExposure.Full, SelfExposure.Full, 1f);

    /// <summary>The reported bug, as a property: a shooter you cannot reach, who is barely hurting
    /// you, does not buy your objective off you.</summary>
    [Fact]
    public void ADistantHarasserIsNotWorthAbandoningAnObjectiveFor()
        => Assert.False(ThreatResponse.IsWorthAnswering(
            Self(ItemType.Ppsh),
            At(200f, ItemType.Mosin),
            objectiveValue: StrategicValue.TicketsPerSecondPerFlag));

    /// <summary>And the other half, which matters just as much: a man shooting at you from across
    /// the street is worth everything you were doing.</summary>
    [Fact]
    public void AThreatInsideItsOwnKillingRangeIsAlwaysWorthAnswering()
        => Assert.True(ThreatResponse.IsWorthAnswering(
            Self(ItemType.Ppsh),
            At(15f, ItemType.Ppsh),
            objectiveValue: StrategicValue.TicketsPerSecondPerFlag));

    /// <summary>The objective's worth has to enter the decision, or this is just a range check with
    /// extra steps. A squad with nothing better to do should go and deal with the sniper.</summary>
    [Fact]
    public void AWorthlessObjectiveLowersTheBarToAnswering()
    {
        var self = Self(ItemType.Sks);
        var threat = At(120f, ItemType.Mosin);

        bool withNothingElseToDo = ThreatResponse.IsWorthAnswering(self, threat, 0f);
        bool withSomethingBetter = ThreatResponse.IsWorthAnswering(self, threat, 10f);

        Assert.True(
            withNothingElseToDo || !withSomethingBetter,
            "the objective's worth must change the answer");
        Assert.False(withSomethingBetter, "a valuable objective is not sold cheaply");
    }

    /// <summary>The bridge between the two currencies. A hundred-health man losing thirty health per
    /// second is dying every 3.3 seconds, and each death costs his team one ticket.</summary>
    [Fact]
    public void HealthPerSecondConvertsToTicketsPerSecondThroughOneTicketPerBody()
    {
        Assert.Equal(0.3f, ThreatResponse.TicketsPerSecond(30f, 100), 4);
        Assert.Equal(0f, ThreatResponse.TicketsPerSecond(0f, 100), 4);
        Assert.Equal(0f, ThreatResponse.TicketsPerSecond(30f, 0), 4);
    }
}
