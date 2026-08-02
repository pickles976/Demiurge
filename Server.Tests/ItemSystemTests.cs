using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

public class ItemSystemTests
{
    [Fact]
    public void AdministrativeEquipDespawnsReplacedItem()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 60000 };

        var first = items.SpawnEquipped(actor, ItemType.Ak47, dropReplaced: false);
        var second = items.SpawnEquipped(actor, ItemType.Glock, dropReplaced: false);

        Assert.False(objects.TryGet(first.NetworkId, out _));
        Assert.True(objects.TryGet(second.NetworkId, out var equipped));
        Assert.Equal(actor.Id, equipped.Owner.PlayerId);
        Assert.Equal(ItemType.Glock, equipped.Item.Type);
        Assert.Equal(second.NetworkId, actor.Equipped[EquipSlot.Hand]);
        Assert.Single(objects.All);
    }

    [Fact]
    public void PickupGetsTraitsFromCatalogType()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);

        var pickup = items.SpawnPickup(ItemType.BodyArmor, new Vector3(1, 2, 3));

        Assert.True(pickup.Has.HasFlag(NetComponents.Item));
        Assert.True(pickup.Has.HasFlag(NetComponents.Transform));
        Assert.True(pickup.Has.HasFlag(NetComponents.Armor));
        Assert.False(pickup.Has.HasFlag(NetComponents.Weapon));
        Assert.Equal(ItemType.BodyArmor, pickup.Item.Type);
        Assert.Equal(new Vector3(1, 2, 3), pickup.Transform.Position);
    }

    [Fact]
    public void HotbarGrenadeStackStartsWithFour()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 7 };
        var grenade = items.SpawnHotbar(actor, ItemType.Grenade, HotbarSlot.Grenade);

        Assert.True(grenade.Has.HasFlag(NetComponents.Weapon));
        Assert.Equal(4, grenade.Weapon.CurrentAmmo);
        Assert.Equal(EquipSlot.HotbarGrenade, grenade.Attachment.Slot);
        Assert.Equal(grenade.NetworkId, actor.Equipped[EquipSlot.HotbarGrenade]);
    }

    [Fact]
    public void InfantryLoadoutStartsWithDefaultRifleShovelAndFourGrenades()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var npc = new ServerPlayer { Id = 60000, IsMob = true };

        items.SpawnInfantryLoadout(npc);

        Assert.Equal(HotbarSlot.Primary, npc.Hotbar);
        Assert.True(objects.TryGet(npc.Equipped[EquipSlot.HotbarPrimary], out var primary));
        Assert.Equal(ItemConfig.DefaultNpcPrimaryWeapon, primary.Item.Type);
        Assert.Equal(
            WeaponConfig.Require(ItemConfig.DefaultNpcPrimaryWeapon).MagazineCapacity,
            primary.Weapon.CurrentAmmo);

        Assert.True(objects.TryGet(npc.Equipped[EquipSlot.HotbarGrenade], out var grenades));
        Assert.Equal(ItemType.Grenade, grenades.Item.Type);
        Assert.Equal(4, grenades.Weapon.CurrentAmmo);

        // The shovel is a real item object now — it is what the hip and the hand render — but it
        // is a tool, not a gun: no WeaponConfig row means no WeaponState bit, so fire and reload
        // skip it while selecting its slot still authorizes digging.
        Assert.True(objects.TryGet(npc.Equipped[EquipSlot.HotbarShovel], out var shovel));
        Assert.Equal(ItemType.Shovel, shovel.Item.Type);
        Assert.False(shovel.Has.HasFlag(NetComponents.Weapon));

        Assert.False(npc.Equipped.ContainsKey(EquipSlot.Hand));
        Assert.True(HotbarConfig.IsValid(HotbarSlot.Shovel));
    }

    [Fact]
    public void PlayerLoadoutStartsWithPpsh()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var player = new ServerPlayer { Id = 7 };

        items.SpawnInfantryLoadout(player);

        Assert.True(objects.TryGet(player.Equipped[EquipSlot.HotbarPrimary], out var primary));
        Assert.Equal(ItemType.Ppsh, primary.Item.Type);
        Assert.Equal(35, primary.Weapon.CurrentAmmo);
    }

    [Fact]
    public void RespawnRefillsEquippedPrimaryAndRecreatesConsumedGrenades()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 7 };
        items.SpawnInfantryLoadout(actor);

        Assert.True(objects.TryGet(actor.Equipped[EquipSlot.HotbarPrimary], out var primary));
        primary.Weapon.CurrentAmmo = 1;
        uint grenadeId = actor.Equipped[EquipSlot.HotbarGrenade];
        Assert.True(items.ConsumeEquipped(actor, EquipSlot.HotbarGrenade, grenadeId));

        items.RefillRespawnLoadout(actor);

        Assert.Equal(
            WeaponConfig.Require(primary.Item.Type).MagazineCapacity,
            primary.Weapon.CurrentAmmo);
        Assert.True(primary.Dirty.HasFlag(NetComponents.Weapon));
        Assert.True(objects.TryGet(actor.Equipped[EquipSlot.HotbarGrenade], out var grenades));
        Assert.Equal(
            WeaponConfig.Require(ItemType.Grenade).MagazineCapacity,
            grenades.Weapon.CurrentAmmo);
        Assert.Equal(HotbarSlot.Primary, actor.Hotbar);
    }
}
