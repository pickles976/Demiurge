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
}
