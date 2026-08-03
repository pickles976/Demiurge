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
}
