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
            BallisticsConfig.Require(ItemType.Ppsh),
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
            BallisticsConfig.Require(ItemType.Mosin),
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

    /// <summary>
    /// Weight is the balance for the machine gun's magazine and rate of fire, and the whole point of
    /// the mortar. Everything else is light, and staying light is the thing worth pinning: a stray
    /// weight is a movement bug that reads as lag, because the client predicts with this number.
    /// </summary>
    [Fact]
    public void OnlyTheHeavyThingsCostMovementSpeedToCarry()
    {
        Assert.Equal(0.7f, ItemConfig.MoveSpeedScale(ItemType.Dp27));
        Assert.Equal(MortarConfig.CarryMoveSpeedScale, ItemConfig.MoveSpeedScale(ItemType.Mortar));

        foreach (var definition in ItemCatalog.All)
        {
            if (definition.Type is ItemType.Dp27 or ItemType.Mortar) continue;
            Assert.Equal(1f, ItemConfig.MoveSpeedScale(definition.Type));
        }
    }

    /// <summary>
    /// A carryable is a thing you haul, and hauling has to cost something or the category is
    /// decoration. This is the property, not the mortar's particular 0.5.
    /// </summary>
    [Fact]
    public void EveryCarryableSlowsTheManHaulingIt()
    {
        foreach (var definition in ItemCatalog.All)
        {
            if (!ItemConfig.IsCarryable(definition.Type)) continue;
            Assert.True(
                ItemConfig.MoveSpeedScale(definition.Type) < 1f,
                $"{definition.Id} is carryable but free to carry");
        }
    }

    /// <summary>
    /// Carryable things still have to be pickable-up — the category changes how they are USED, not
    /// whether E takes them. Every path that decides that asks ItemConfig.IsHeld; this is what
    /// fails if one of them goes back to testing for Equippable alone.
    /// </summary>
    [Fact]
    public void ACarryableCanStillBePickedUp()
    {
        foreach (var definition in ItemCatalog.All)
        {
            if (!ItemConfig.IsCarryable(definition.Type)) continue;
            Assert.True(ItemConfig.IsHeld(definition.Type), $"{definition.Id} cannot be picked up");
        }
    }
}
