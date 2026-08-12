namespace Demiurge.Tests;

/// <summary>
/// Properties of the skill spread, not its magnitudes — the band is tuning surface and will move.
/// </summary>
public class MarksmanshipTests
{
    [Fact]
    public void EveryManLandsInsideTheBand()
    {
        for (int actorId = 1; actorId <= 2000; actorId++)
        {
            float skill = Marksmanship.SkillFactorFor(seed: 0x51A7, actorId);
            Assert.InRange(skill, Marksmanship.BestSkillFactor, Marksmanship.WorstSkillFactor);
        }
    }

    /// <summary>
    /// The point of the spread: a squad must not be eight identical shooters. Eight men is the
    /// roster size, so this is asked of the smallest group that has to vary.
    /// </summary>
    [Fact]
    public void ASquadIsNotEightIdenticalShooters()
    {
        var squad = Enumerable.Range(60_000, 8)
            .Select(actorId => Marksmanship.SkillFactorFor(seed: 0x51A7, actorId))
            .ToArray();

        Assert.True(
            squad.Max() - squad.Min() > 0.2f,
            $"a squad spanning {squad.Min():F2}-{squad.Max():F2} is effectively uniform");
    }

    /// <summary>
    /// A man's skill cannot depend on what else drew a random number first, or a scenario stops
    /// being reproducible the moment anything upstream of it consumes one.
    /// </summary>
    [Fact]
    public void OneManAlwaysShootsTheSame()
    {
        Assert.Equal(
            Marksmanship.SkillFactorFor(seed: 7, actorId: 60_123),
            Marksmanship.SkillFactorFor(seed: 7, actorId: 60_123));
        Assert.NotEqual(
            Marksmanship.SkillFactorFor(seed: 7, actorId: 60_123),
            Marksmanship.SkillFactorFor(seed: 8, actorId: 60_123));
    }

    /// <summary>Across a map's worth of men the spread has to average near its midpoint, or the
    /// band is not the thing being tuned.</summary>
    [Fact]
    public void TheForceAveragesTheMiddleOfTheBand()
    {
        float mean = Enumerable.Range(60_000, 512)
            .Select(actorId => Marksmanship.SkillFactorFor(seed: 0x51A7, actorId))
            .Average();
        float midpoint = (Marksmanship.BestSkillFactor + Marksmanship.WorstSkillFactor) * 0.5f;

        Assert.InRange(mean, midpoint - 0.1f, midpoint + 0.1f);
    }
}
