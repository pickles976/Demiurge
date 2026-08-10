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
        float sniper = BallisticsConfig.Get("demiurge:sniper_rifle").SightingMoa;
        float semiAuto = BallisticsConfig.Get("demiurge:semi_automatic_rifle").SightingMoa;
        float carbine = BallisticsConfig.Get("demiurge:carbine").SightingMoa;
        float pistol = BallisticsConfig.Get("demiurge:pistol").SightingMoa;

        Assert.True(sniper < semiAuto, "a scoped bolt gun must aim tighter than a semi-automatic rifle");
        Assert.True(semiAuto < carbine, "a marksman rifle must aim tighter than a carbine");
        Assert.True(carbine < pistol, "a carbine must aim tighter than a pistol-calibre weapon");
    }

    [Fact]
    public void SightingErrorDominatesBenchDispersionForAHumanShooter()
    {
        // The point of the field: bench dispersion (2-8 MOA) is irrelevant next to how well a
        // person can hold the sights. If these were the same order, the field would be pointless.
        var carbine = BallisticsConfig.Get("demiurge:carbine");
        Assert.True(
            carbine.SightingMoa > carbine.BenchMoa * 10f,
            $"sighting {carbine.SightingMoa} should dwarf bench {carbine.BenchMoa}");
    }

    [Fact]
    public void ThrowableHasNoSightingError()
        => Assert.Equal(0f, BallisticsConfig.Get("demiurge:throwable").SightingMoa);

    /// <summary>
    /// Where each weapon wants to be fought, derived from its own damage curve rather than written
    /// down per weapon. The ordering IS the doctrine — an SMG closes, a bolt gun holds — and nothing
    /// in the AI has to know which is which.
    /// </summary>
    [Fact]
    public void PreferredRangeOrdersWeaponsFromSubmachineGunToBoltAction()
    {
        float smg = WeaponEffectiveness.PreferredRange(ItemType.Ppsh, 1f);
        float carbine = WeaponEffectiveness.PreferredRange(ItemType.Sks, 1f);
        float rifle = WeaponEffectiveness.PreferredRange(ItemType.Mosin, 1f);

        Assert.True(smg > 0f);
        Assert.True(smg < carbine, $"smg {smg} should want to be closer than carbine {carbine}");
        Assert.True(carbine < rifle, $"carbine {carbine} should want to be closer than rifle {rifle}");
    }
}
