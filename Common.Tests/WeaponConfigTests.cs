namespace Demiurge.Tests;

public class WeaponConfigTests
{
    [Fact]
    public void PpshHasRequestedMagazineCadenceReloadAndDamage()
    {
        var stats = WeaponConfig.Require(ItemType.Ppsh);

        Assert.Equal(35, stats.MagazineCapacity);
        Assert.Equal(NetworkConfig.TickRate / 20f, stats.TicksPerShot);
        Assert.Equal(3 * NetworkConfig.TickRate / 2, stats.ReloadTicks);
        Assert.Equal((ushort)18, stats.Damage);
        Assert.Equal(FireMode.Automatic, stats.FireMode);
    }

    [Fact]
    public void PpshUsesTheNineMillimetreBallisticsAndRecoilProfile()
    {
        Assert.Equal(
            BallisticsConfig.Require(ItemType.Glock),
            BallisticsConfig.Require(ItemType.Ppsh));
    }

    /// <summary>Asserted in SECONDS rather than ticks, because seconds are what was asked for and
    /// the tick arithmetic is the part that can be got wrong.</summary>
    [Fact]
    public void MosinHasRequestedMagazineCadenceReloadAndDamage()
    {
        var stats = WeaponConfig.Require(ItemType.Mosin);

        Assert.Equal(5, stats.MagazineCapacity);
        Assert.Equal(1.5f, stats.TicksPerShot / NetworkConfig.TickRate);
        Assert.Equal(5.07f, stats.ReloadTicks / (float)NetworkConfig.TickRate, precision: 2);
        Assert.Equal((ushort)70, stats.Damage);
        Assert.Equal(FireMode.SemiAutomatic, stats.FireMode);
    }

    [Fact]
    public void MosinUsesTheSniperBallisticsAndRecoilProfile()
    {
        Assert.Equal(
            BallisticsConfig.Require(ItemType.AWP),
            BallisticsConfig.Require(ItemType.Mosin));
    }

    /// <summary>Cadence asserted as ROUNDS PER MINUTE, which is the number the gun is specified in;
    /// 550 is not a whole number of ticks and the fractional cadence is what preserves it.</summary>
    [Fact]
    public void Dp27HasRequestedMagazineCadenceAndDamage()
    {
        var stats = WeaponConfig.Require(ItemType.Dp27);

        Assert.Equal(47, stats.MagazineCapacity);
        Assert.Equal(550f, 60f * NetworkConfig.TickRate / stats.TicksPerShot, precision: 3);
        Assert.Equal((ushort)50, stats.Damage);
        Assert.Equal(FireMode.Automatic, stats.FireMode);
    }

    /// <summary>Same cartridge, shorter barrel: every ballistic term is the bolt gun's, moved the
    /// wrong way. A property of the model rather than the specific numbers, so retuning the profile
    /// cannot quietly make the machine gun the better rifle.</summary>
    [Fact]
    public void Dp27ShootsSlightlyWorseThanTheMosinItSharesACartridgeWith()
    {
        var mosin = BallisticsConfig.Require(ItemType.Mosin);
        var dp27 = BallisticsConfig.Require(ItemType.Dp27);

        Assert.True(dp27.ProjectileSpeed < mosin.ProjectileSpeed);
        Assert.True(dp27.BenchMoa > mosin.BenchMoa);
        Assert.True(dp27.SightingMoa > mosin.SightingMoa);
    }

    /// <summary>The weight is the balance for the magazine and the rate of fire, so it is worth
    /// pinning that the machine gun is the only thing in the game that carries one.</summary>
    [Fact]
    public void OnlyTheDp27CostsMovementSpeedToCarry()
    {
        Assert.Equal(0.7f, WeaponConfig.MoveSpeedScale(ItemType.Dp27));

        foreach (var definition in ItemCatalog.All)
        {
            if (definition.Type == ItemType.Dp27) continue;
            Assert.Equal(1f, WeaponConfig.MoveSpeedScale(definition.Type));
        }
    }
}
