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
    public void HotbarGrenadeStackStartsAtItsCapacity()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 7 };
        var grenade = items.SpawnHotbar(actor, ItemType.Grenade, HotbarSlot.Grenade);

        Assert.True(grenade.Has.HasFlag(NetComponents.Weapon));
        // The stack's capacity IS the issue, so this reads it from the table rather than restating
        // it — the number itself is pinned once, in WeaponConfigTests.
        Assert.Equal(WeaponConfig.Require(ItemType.Grenade).MagazineCapacity, grenade.Weapon.CurrentAmmo);
        Assert.Equal(EquipSlot.HotbarGrenade, grenade.Attachment.Slot);
        Assert.Equal(grenade.NetworkId, actor.Equipped[EquipSlot.HotbarGrenade]);
    }

    [Fact]
    public void InfantryLoadoutStartsWithDefaultRifleAndShovel()
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
    public void PlayerLoadoutStartsWithTheDefaultPrimaryAtFullMagazine()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var player = new ServerPlayer { Id = 7 };

        items.SpawnInfantryLoadout(player);

        Assert.True(objects.TryGet(player.Equipped[EquipSlot.HotbarPrimary], out var primary));
        Assert.Equal(ItemConfig.DefaultPlayerPrimaryWeapon, primary.Item.Type);
        Assert.Equal(
            WeaponConfig.Require(ItemConfig.DefaultPlayerPrimaryWeapon).MagazineCapacity,
            primary.Weapon.CurrentAmmo);
    }

    /// <summary>
    /// Grenades follow the assault gun, not the man. Asserted as the rule over every primary the
    /// spawn cohort issues rather than against one weapon, so changing which gun the assaulters
    /// carry moves this with it instead of leaving a stale name behind.
    /// </summary>
    [Theory]
    [InlineData(ItemType.Ppsh, true)]
    [InlineData(ItemType.Sks, false)]
    [InlineData(ItemType.Mosin, false)]
    public void OnlyAssaultNpcsAreIssuedGrenades(ItemType primary, bool expected)
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var npc = new ServerPlayer { Id = 60000, IsMob = true };

        items.SpawnInfantryLoadout(npc, primary);

        Assert.Equal(expected, npc.Equipped.ContainsKey(EquipSlot.HotbarGrenade));
    }

    /// <summary>A player is not a squad role: nobody assigns him a weapon, so nothing about what he
    /// picked up decides whether he has grenades.</summary>
    [Theory]
    [InlineData(ItemType.Ppsh)]
    [InlineData(ItemType.Mosin)]
    public void PlayersAlwaysCarryGrenadesWhateverTheirPrimary(ItemType primary)
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var player = new ServerPlayer { Id = 7 };

        items.SpawnInfantryLoadout(player, primary);

        Assert.True(player.Equipped.ContainsKey(EquipSlot.HotbarGrenade));
    }

    /// <summary>
    /// The respawn path applies the same rule as the spawn path. These were three duplicated lines
    /// apiece before, which is exactly how a man ends up with a different loadout after his first
    /// death than he started the round with.
    /// </summary>
    [Fact]
    public void RespawnIssuesGrenadesByTheSameRuleAsSpawning()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var rifleman = new ServerPlayer { Id = 60000, IsMob = true };
        var assaulter = new ServerPlayer { Id = 60001, IsMob = true };

        items.SpawnInfantryLoadout(rifleman, ItemType.Sks);
        items.SpawnInfantryLoadout(assaulter, NpcSquadLoadout.AssaultWeapon);
        items.RefillRespawnLoadout(rifleman);
        items.RefillRespawnLoadout(assaulter);

        Assert.False(rifleman.Equipped.ContainsKey(EquipSlot.HotbarGrenade));
        Assert.True(assaulter.Equipped.ContainsKey(EquipSlot.HotbarGrenade));
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
