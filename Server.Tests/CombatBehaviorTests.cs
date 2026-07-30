using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class CombatBehaviorTests
{
    [Theory]
    [InlineData(0.10f, 60f, true)]
    [InlineData(0.60f, 60f, false)]
    [InlineData(0.10f, 15f, false)]
    public void LowProbabilityFireClosesOnlyFromLongRange(
        float probability,
        float range,
        bool expected)
        => Assert.Equal(
            expected,
            CombatBehavior.ShouldAdvance(probability, range));

    [Fact]
    public void AimBecomesTighterAsRangeIncreases()
    {
        float close = CombatBehavior.AimMoaForRange(20f);
        float medium = CombatBehavior.AimMoaForRange(50f);
        float far = CombatBehavior.AimMoaForRange(90f);

        Assert.True(close > medium);
        Assert.True(medium > far);
        Assert.Equal(far, CombatBehavior.AimMoaForRange(200f));
    }
}
