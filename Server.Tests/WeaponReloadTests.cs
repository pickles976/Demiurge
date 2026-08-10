using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

/// <summary>
/// Reloading against a finite reserve. The arithmetic is small and the two ways it goes wrong are
/// both silent: topping up to capacity regardless of the pouches gives a player infinite ammunition,
/// and refilling rather than topping up quietly throws away the rounds still in his magazine.
/// </summary>
public class WeaponReloadTests
{
    [Fact]
    public void ReloadingTopsTheMagazineUpFromTheReserveWithoutWastingWhatIsInIt()
    {
        var (weapons, items, objects) = Fixture();
        var player = new ServerPlayer { Id = 1 };
        items.SpawnInfantryLoadout(player);

        Assert.True(objects.TryGet(player.Equipped[EquipSlot.HotbarPrimary], out var rifle));
        int capacity = WeaponConfig.Require(rifle.Item.Type).MagazineCapacity;
        int reserve = rifle.Weapon.ReserveAmmo;
        rifle.Weapon.CurrentAmmo = capacity - 3;

        weapons.ApplyReload(player, tick: 10);

        Assert.Equal(capacity, rifle.Weapon.CurrentAmmo);
        Assert.Equal(reserve - 3, rifle.Weapon.ReserveAmmo);
    }

    [Fact]
    public void AManOutOfPouchesKeepsWhatIsLoadedAndCannotReload()
    {
        var (weapons, items, objects) = Fixture();
        var player = new ServerPlayer { Id = 1 };
        items.SpawnInfantryLoadout(player);

        Assert.True(objects.TryGet(player.Equipped[EquipSlot.HotbarPrimary], out var rifle));
        rifle.Weapon.CurrentAmmo = 2;
        rifle.Weapon.ReserveAmmo = 0;

        weapons.ApplyReload(player, tick: 10);

        Assert.Equal(2, rifle.Weapon.CurrentAmmo);
        Assert.Equal(0, rifle.Weapon.ReserveAmmo);
        // No reload window either: a man with nothing to load should not be locked out of firing
        // the rounds he still has.
        Assert.Equal(0u, player.ReloadDoneTick);
    }

    /// <summary>
    /// NPCs have infinite ammunition, permanently and by design. The reserve on their weapons is
    /// still real, and this is what it is FOR: a gun taken off a dead NPC is always worth a full
    /// load, because the man carrying it never drew it down.
    /// </summary>
    [Fact]
    public void AnNpcReloadsWithoutSpendingItsReserve()
    {
        var (weapons, items, objects) = Fixture();
        var mob = new ServerPlayer { Id = 60000, IsMob = true };
        items.SpawnInfantryLoadout(mob);

        Assert.True(objects.TryGet(mob.Equipped[EquipSlot.HotbarPrimary], out var rifle));
        int capacity = WeaponConfig.Require(rifle.Item.Type).MagazineCapacity;
        int reserve = rifle.Weapon.ReserveAmmo;
        Assert.True(reserve > 0, "an NPC's rifle carries a real load, it just never spends it");
        rifle.Weapon.CurrentAmmo = 0;

        weapons.ApplyReload(mob, tick: 10);

        Assert.Equal(capacity, rifle.Weapon.CurrentAmmo);
        Assert.Equal(reserve, rifle.Weapon.ReserveAmmo);
    }

    private static (WeaponSystem, ItemSystem, ObjectReplication) Fixture()
    {
        var server = new NullNetServer();
        var objects = new ObjectReplication(server);
        return (new WeaponSystem(server, objects, new ChunkMap()), new ItemSystem(objects), objects);
    }
}
