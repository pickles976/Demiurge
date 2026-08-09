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

        var first = items.SpawnEquipped(actor, ItemType.Sks, dropReplaced: false);
        var second = items.SpawnEquipped(actor, ItemType.Ppsh, dropReplaced: false);

        Assert.False(objects.TryGet(first.NetworkId, out _));
        Assert.True(objects.TryGet(second.NetworkId, out var equipped));
        Assert.Equal(actor.Id, equipped.Owner.PlayerId);
        Assert.Equal(ItemType.Ppsh, equipped.Item.Type);
        Assert.Equal(second.NetworkId, actor.Equipped[EquipSlot.Hand]);
        Assert.Single(objects.All);
    }

    /// <summary>
    /// Picking something heavy up does not make you throw your rifle away. It fills your hands: the
    /// kit stays exactly where it was and simply cannot be reached, which is what IsCarrying means.
    /// </summary>
    [Fact]
    public void HaulingSomethingKeepsYourWeaponsAndLocksThemAway()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 1 };
        items.SpawnInfantryLoadout(actor);
        uint rifleId = actor.Equipped[EquipSlot.HotbarPrimary];

        var mortar = items.SpawnPickup(ItemType.Mortar, actor.Position);
        items.ApplyInteract(actor);

        Assert.True(actor.IsCarrying);
        Assert.Equal(EquipSlot.Carried, objects.All
            .Single(o => o.Has.HasFlag(NetComponents.Owner) && o.Item.Type == ItemType.Mortar)
            .Attachment.Slot);

        // The rifle is untouched — same object, same slot, still owned.
        Assert.Equal(rifleId, actor.Equipped[EquipSlot.HotbarPrimary]);
        Assert.True(objects.TryGet(rifleId, out _));

        // And nothing of it was dropped on the ground in the process.
        Assert.DoesNotContain(
            objects.All,
            o => o.Has.HasFlag(NetComponents.Transform) && o.Has.HasFlag(NetComponents.Item));

        Assert.False(mortar.Has == default);   // the pickup existed before it was taken
    }

    /// <summary>
    /// Putting something down is an act of aiming, not of tidying up: the heading it lands on is the
    /// line a mortar's tube will traverse around, so it has to come from where the man was looking.
    /// </summary>
    [Fact]
    public void PuttingSomethingDownEmplacesItOnTheHeadingYouWereFacing()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 1, Yaw = 1.25f };
        actor.Move.Position = new Vector3(10f, 4f, -7f);
        items.SpawnPickup(ItemType.Mortar, actor.Position);
        items.ApplyInteract(actor);
        Assert.True(actor.IsCarrying);

        actor.Yaw = -2.5f;                       // turned around before setting it down
        items.ApplyInteract(actor);              // E again: put it down

        Assert.False(actor.IsCarrying);
        var emplaced = objects.All.Single(o => o.Item.Type == ItemType.Mortar);
        Assert.True(emplaced.Has.HasFlag(NetComponents.Transform));
        Assert.False(emplaced.Has.HasFlag(NetComponents.Owner));
        Assert.Equal(actor.Position, emplaced.Transform.Position);
        Assert.Equal(-2.5f, emplaced.Transform.Yaw, 4);
    }

    /// <summary>A man with his hands full cannot change what is selected.</summary>
    [Fact]
    public void CarryingRefusesHotbarChanges()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 1 };
        items.SpawnInfantryLoadout(actor);

        items.SelectHotbar(actor, HotbarSlot.Shovel);
        Assert.Equal(HotbarSlot.Shovel, actor.Hotbar);

        items.SpawnPickup(ItemType.Mortar, actor.Position);
        items.ApplyInteract(actor);

        items.SelectHotbar(actor, HotbarSlot.Primary);
        Assert.Equal(HotbarSlot.Shovel, actor.Hotbar);
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

    /// <summary>
    /// A dead man leaves his rifle and nothing else. The rest of his kit staying equipped is the
    /// half that is easy to lose: dropping the lot would strip his armour and carpet the ground in
    /// shovels, and neither is what "drop your weapon" means.
    /// </summary>
    [Fact]
    public void DeathLeavesTheWeaponHeWasHoldingAndKeepsTheRestOfTheKit()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 3, Move = new MoveState { Position = new Vector3(5f, 0f, 9f) } };
        items.SpawnInfantryLoadout(actor);

        // Killed part way through a magazine and part way through his pouches.
        Assert.True(objects.TryGet(actor.Equipped[EquipSlot.HotbarPrimary], out var carried));
        carried.Weapon.CurrentAmmo = 2;
        carried.Weapon.ReserveAmmo = 13;

        items.DropOnDeath(actor);

        Assert.False(actor.Equipped.ContainsKey(EquipSlot.HotbarPrimary));
        Assert.True(actor.Equipped.ContainsKey(EquipSlot.HotbarShovel));
        Assert.True(actor.Equipped.ContainsKey(EquipSlot.HotbarGrenade));

        var onGround = Assert.Single(
            objects.All,
            o => o.Has.HasFlag(NetComponents.Transform) && o.Has.HasFlag(NetComponents.Item));
        Assert.Equal(carried.Item.Type, onGround.Item.Type);
        Assert.Equal(actor.Position, onGround.Transform.Position);

        // The gun as he left it: taking it is a gamble on what he had spent.
        Assert.Equal(2, onGround.Weapon.CurrentAmmo);
        Assert.Equal(13, onGround.Weapon.ReserveAmmo);
    }

    /// <summary>
    /// Litter rots; scenery does not. Both are pickups sitting in the world with identical masks, so
    /// the only thing separating them is how they got there.
    /// </summary>
    [Fact]
    public void DroppedWeaponsExpireAndPlacedOnesDoNot()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 4 };
        items.SpawnInfantryLoadout(actor);

        items.Tick(100);
        items.DropOnDeath(actor);
        var placed = items.SpawnPickup(ItemType.Sks, new Vector3(20f, 0f, 20f));

        uint dropped = Assert.Single(
            objects.All,
            o => o.Has.HasFlag(NetComponents.Transform)
                 && o.Has.HasFlag(NetComponents.Item)
                 && o.NetworkId != placed.NetworkId).NetworkId;

        items.Tick(100 + (uint)ItemConfig.DroppedLifetimeTicks - 1);
        Assert.True(objects.TryGet(dropped, out _));

        items.Tick(100 + (uint)ItemConfig.DroppedLifetimeTicks);
        Assert.False(objects.TryGet(dropped, out _));
        Assert.True(objects.TryGet(placed.NetworkId, out _));
    }

    /// <summary>
    /// A man is issued <see cref="ItemConfig.SpareMagazines"/> magazines for his pouches ON TOP of
    /// the full one in the weapon, so he can reload that many times. The grenade stack is issued
    /// none — its magazine is the grenades themselves and no reload could ever reach a reserve
    /// behind it.
    /// </summary>
    [Fact]
    public void ALoadoutIsAFullWeaponPlusFiveSpareMagazines()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 5 };
        items.SpawnInfantryLoadout(actor);

        Assert.True(objects.TryGet(actor.Equipped[EquipSlot.HotbarPrimary], out var primary));
        int capacity = WeaponConfig.Require(primary.Item.Type).MagazineCapacity;
        Assert.Equal(capacity, primary.Weapon.CurrentAmmo);
        Assert.Equal(capacity * ItemConfig.SpareMagazines, primary.Weapon.ReserveAmmo);

        Assert.True(objects.TryGet(actor.Equipped[EquipSlot.HotbarGrenade], out var grenades));
        Assert.Equal(0, grenades.Weapon.ReserveAmmo);
    }
}
