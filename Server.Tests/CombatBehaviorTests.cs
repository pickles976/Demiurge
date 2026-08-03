using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class CombatBehaviorTests
{
    [Fact]
    public void IncomingFireCreatesABoundedDefensiveWindow()
    {
        var brain = new MobBrain();

        brain.MarkUnderFire(100);

        Assert.True(brain.IsUnderFire(100));
        Assert.True(brain.IsUnderFire(
            100 + MobBrain.IncomingFireResponseTicks - 1));
        Assert.False(brain.IsUnderFire(
            100 + MobBrain.IncomingFireResponseTicks));
    }

    [Theory]
    [InlineData(0.10f, 60f, true)]
    [InlineData(0.10f, 35f, true)]
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
        Assert.Equal(720f, close);
        Assert.Equal(180f, far);
        Assert.Equal(far, CombatBehavior.AimMoaForRange(200f));
    }

    [Theory]
    [InlineData(ItemType.Ppsh, 76f, true)]
    [InlineData(ItemType.Ppsh, 75f, false)]
    [InlineData(ItemType.Ppsh, 55f, false)]
    [InlineData(ItemType.Ppsh, 40f, false)]
    [InlineData(ItemType.Ppsh, 20f, false)]
    [InlineData(ItemType.Sks, 40f, false)]
    public void PpshHoldsFireUntilItsEffectiveRange(
        ItemType weapon,
        float range,
        bool expected)
        => Assert.Equal(expected, CombatBehavior.PrefersToHoldFire(weapon, range));

    [Theory]
    [InlineData(ItemType.Mosin, 150f)]
    [InlineData(ItemType.Sks, 100f)]
    [InlineData(ItemType.Ppsh, 75f)]
    [InlineData(ItemType.Ak47, 70f)]
    public void EngagementCeilingIsWeaponSpecific(ItemType weapon, float expected)
        => Assert.Equal(expected, CombatBehavior.MaxEngagementRangeFor(weapon));

    [Theory]
    [InlineData(99f, true, true)]
    [InlineData(101f, true, false)]
    [InlineData(50f, false, false)]
    public void EnemyReloadTellIsKnownOnlyWhileReloadingWithinOneHundredMetres(
        float range,
        bool reloading,
        bool expected)
    {
        var observer = new ServerPlayer { Move = new MoveState { Position = Vector3.Zero } };
        var enemy = new ServerPlayer
        {
            Move = new MoveState { Position = new Vector3(0f, 0f, range) },
            State = reloading ? PlayerStateFlags.Reloading : PlayerStateFlags.None,
        };

        Assert.Equal(expected, MobSystem.CanRecognizeEnemyReload(observer, enemy));
    }
}
