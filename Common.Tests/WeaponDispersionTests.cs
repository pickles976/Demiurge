namespace Demiurge.Tests;

/// <summary>
/// The aiming error is a property of the weapon-shooter SYSTEM — sight radius, sight picture,
/// trigger, weight — not a constant of the shooter. A flat AI aim constant makes every weapon
/// identical, which is why weapon character had to be reintroduced as ItemType branches.
/// </summary>
public class WeaponDispersionTests
{
    [Fact]
    public void SightingErrorOrdersWeaponsFromPrecisionToSpray()
    {
        float sniper = BallisticsConfig.Get(WeaponBallisticsProfile.SniperRifle).SightingMoa;
        float semiAuto = BallisticsConfig.Get(WeaponBallisticsProfile.SemiAutomaticRifle).SightingMoa;
        float carbine = BallisticsConfig.Get(WeaponBallisticsProfile.Carbine).SightingMoa;
        float pistol = BallisticsConfig.Get(WeaponBallisticsProfile.Pistol).SightingMoa;

        Assert.True(sniper < semiAuto, "a scoped bolt gun must aim tighter than a semi-automatic rifle");
        Assert.True(semiAuto < carbine, "a marksman rifle must aim tighter than a carbine");
        Assert.True(carbine < pistol, "a carbine must aim tighter than a pistol-calibre weapon");
    }

    [Fact]
    public void SightingErrorDominatesBenchDispersionForAHumanShooter()
    {
        // The point of the field: bench dispersion (2-8 MOA) is irrelevant next to how well a
        // person can hold the sights. If these were the same order, the field would be pointless.
        var carbine = BallisticsConfig.Get(WeaponBallisticsProfile.Carbine);
        Assert.True(
            carbine.SightingMoa > carbine.BenchMoa * 10f,
            $"sighting {carbine.SightingMoa} should dwarf bench {carbine.BenchMoa}");
    }

    [Fact]
    public void ThrowableHasNoSightingError()
        => Assert.Equal(0f, BallisticsConfig.Get(WeaponBallisticsProfile.Throwable).SightingMoa);
}
