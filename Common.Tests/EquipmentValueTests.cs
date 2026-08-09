namespace Demiurge.Tests;

public class EquipmentValueTests
{
    [Fact]
    public void AWeaponIsNeverWorthSwappingForItself()
        => Assert.False(EquipmentValue.IsWorthTaking(
            ItemType.Sks, ItemType.Sks, 60f, 0f));

    [Fact]
    public void ALongWalkCanConsumeAnOtherwiseUsefulUpgrade()
    {
        float near = EquipmentValue.NetGain(ItemType.Ppsh, ItemType.Dp27, 80f, 0f);
        float far = EquipmentValue.NetGain(ItemType.Ppsh, ItemType.Dp27, 80f, 120f);

        Assert.True(near > far);
    }

    [Fact]
    public void Dp27IsAUsefulLongRangeUpgradeOverPpsh()
        => Assert.True(EquipmentValue.IsWorthTaking(
            ItemType.Ppsh, ItemType.Dp27, 80f, 0f));

    [Fact]
    public void NonFirearmsAreNotPrimaryWeaponUpgrades()
        => Assert.False(EquipmentValue.IsWorthTaking(
            ItemType.Sks, ItemType.Mortar, 80f, 0f));
}
