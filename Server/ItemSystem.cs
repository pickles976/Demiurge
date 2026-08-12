using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>Handles item transitions without owning storage. Pickups use Item|Transform; equipped
    /// items use Item|Owner, preserving all trait bits across transitions.</summary>
    public class ItemSystem
    {
        private readonly ObjectReplication objects;
        internal ObjectReplication Objects => objects;

        /// <summary>How a replicated object looks to <see cref="PickupTargeting"/>.</summary>
        private static PickupTargeting.Candidate Describe(ServerObject obj)
            => new(obj.Has, obj.Item.Type, obj.Transform.Position);

        public ItemSystem(ObjectReplication objects) => this.objects = objects;

        /// <summary>
        /// Server tick captured by <see cref="Tick"/> for drop-expiry deadlines.
        /// </summary>
        private uint now;

        /// <summary>
        /// Removes expired dropped items. Map items and deliberate placements have no deadline.
        /// </summary>
        public void Tick(uint tick)
        {
            now = tick;

            List<uint>? expired = null;
            foreach (var obj in objects.All)
            {
                if (obj.DespawnAtTick == 0 || tick < obj.DespawnAtTick) continue;
                (expired ??= []).Add(obj.NetworkId);
            }
            if (expired is null) return;

            // Collected first: Despawn mutates the dictionary All enumerates.
            foreach (uint id in expired) objects.Despawn(id);
        }

        public ServerObject SpawnPickup(ItemType type, Vector3 position)
            => SpawnPickup(type, position, ObjectType.Item);

        /// <summary>
        /// Spawns a map-authored pickup displayed as a crate until first taken.
        /// </summary>
        public ServerObject SpawnSupplyCrate(ItemType type, Vector3 position)
            => SpawnPickup(type, position, ObjectType.Crate);

        private ServerObject SpawnPickup(ItemType type, Vector3 position, ObjectType presentation)
        {
            // The trait tables are the mask recipe: a WeaponConfig row means a
            // WeaponState bit, an ArmorConfig row an ArmorState bit, and so on.
            var weapon = WeaponConfig.Get(type);
            var armor = ArmorConfig.Get(type);
            var mask = NetComponents.Item | NetComponents.Transform;
            if (weapon != null) mask |= NetComponents.Weapon;
            if (armor != null) mask |= NetComponents.Armor;

            return objects.Spawn(presentation, mask, position, obj =>
            {
                obj.Item = new ItemState { Type = type };
                if (weapon is { } w)
                    obj.Weapon = new WeaponState
                    {
                        CurrentAmmo = w.MagazineCapacity,
                        ReserveAmmo = ReserveFor(type, w),
                    };
                if (armor is { } a) obj.Armor = new ArmorState { MaxValue = a.Max, Current = a.Max };
            });
        }

        /// <summary>
        /// Initial reserve rounds. Non-reloadable stacks keep all ammunition in the magazine.
        /// </summary>
        private static int ReserveFor(ItemType type, in WeaponStats weapon)
            => ItemCatalog.HasBehavior(type, ItemBehavior.Grenade)
                ? 0
                : weapon.MagazineCapacity * ItemConfig.SpareMagazines;

        public ServerObject SpawnEquipped(ServerPlayer player, ItemType type, bool dropReplaced = true)
            => SpawnOwned(player, type, ItemConfig.Get(type).Slot, null, dropReplaced);

        /// <summary>Creates one persistent hotbar stack. Only the selected slot is usable/rendered.</summary>
        public ServerObject SpawnHotbar(
            ServerPlayer player,
            ItemType type,
            HotbarSlot hotbar,
            int? ammo = null,
            bool dropReplaced = false)
            => SpawnOwned(player, type, HotbarConfig.StorageSlot(hotbar), ammo, dropReplaced);

        /// <summary>
        /// Standard infantry inventory. The shovel is renderable but has no WeaponState; selecting
        /// its slot authorizes digging.
        /// </summary>
        internal void SpawnInfantryLoadout(ServerPlayer actor, ItemType? primaryWeapon = null)
        {
            ItemType primary = primaryWeapon ?? DefaultPrimary(actor);
            SpawnHotbar(actor, primary, HotbarSlot.Primary);
            SpawnHotbar(actor, ItemCatalog.RequireBehavior(ItemBehavior.Shovel), HotbarSlot.Shovel);
            if (CarriesGrenades(actor, primary)) SpawnGrenades(actor);
            actor.Hotbar = HotbarSlot.Primary;
        }

        /// <summary>
        /// Default primary for initial spawn and respawn.
        /// </summary>
        private static ItemType DefaultPrimary(ServerPlayer actor)
            => actor.IsMob
                ? ItemConfig.DefaultNpcPrimaryWeapon
                : PlayerClasses.Weapon(actor.Class);

        /// <summary>
        /// Shared spawn/respawn grenade issue rule. Players always qualify; NPC issue follows primary role.
        /// </summary>
        private static bool CarriesGrenades(ServerPlayer actor, ItemType primary)
            => !actor.IsMob || NpcSquadLoadout.CarriesGrenades(primary);

        private void SpawnGrenades(ServerPlayer actor)
        {
            ItemType grenade = ItemCatalog.RequireBehavior(ItemBehavior.Grenade);
            SpawnHotbar(
                actor,
                grenade,
                HotbarSlot.Grenade,
                ammo: WeaponConfig.Require(grenade).MagazineCapacity);
        }

        /// <summary>
        /// Refills retained weapons and recreates missing default slots after death. The dropped primary
        /// is replaced with standard issue.
        /// </summary>
        internal void RefillRespawnLoadout(ServerPlayer actor)
        {
            bool hasPrimary = false;
            bool hasShovel = false;
            bool hasGrenades = false;
            ItemType primary = DefaultPrimary(actor);
            foreach (var pair in actor.Equipped)
            {
                if (!objects.TryGet(pair.Value, out var item)
                    || !item.Has.HasFlag(NetComponents.Item))
                    continue;

                // The shovel is the one default slot with no magazine to refill, so it is checked
                // before the weapon filter rather than inside it.
                hasShovel |= pair.Key == EquipSlot.HotbarShovel;

                if (!item.Has.HasFlag(NetComponents.Weapon)
                    || WeaponConfig.Get(item.Item.Type) is not { } weapon)
                    continue;

                item.Weapon.CurrentAmmo = weapon.MagazineCapacity;
                item.Weapon.ReserveAmmo = ReserveFor(item.Item.Type, weapon);
                item.Dirty |= NetComponents.Weapon;
                if (pair.Key is EquipSlot.HotbarPrimary or EquipSlot.Hand)
                {
                    hasPrimary = true;
                    // What he is carrying NOW, not what he spawned with: a mob that picked up a
                    // PPSH is an assaulter for the purposes of the rule below.
                    primary = item.Item.Type;
                }
                hasGrenades |= pair.Key == EquipSlot.HotbarGrenade
                    && ItemCatalog.HasBehavior(item.Item.Type, ItemBehavior.Grenade);
            }

            if (!hasPrimary) SpawnHotbar(actor, primary, HotbarSlot.Primary);
            if (!hasShovel)
                SpawnHotbar(actor, ItemCatalog.RequireBehavior(ItemBehavior.Shovel), HotbarSlot.Shovel);

            // A stack he still has was refilled by the loop above, whoever he is. This only decides
            // who is ISSUED a new one, so a man who came by grenades some other way keeps them
            // rather than having them taken off him at the respawn wave.
            if (!hasGrenades && CarriesGrenades(actor, primary)) SpawnGrenades(actor);
            actor.Hotbar = HotbarSlot.Primary;
        }

        private ServerObject SpawnOwned(
            ServerPlayer player,
            ItemType type,
            EquipSlot slot,
            int? ammo,
            bool dropReplaced)
        {
            var stats = ItemConfig.Get(type);
            if (!ItemConfig.IsHeld(type))
                throw new InvalidOperationException($"{type} cannot be equipped");

            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
            {
                if (dropReplaced)
                    Drop(current, player.Position, player.Yaw, LitterDeadline());
                else
                    objects.Despawn(current.NetworkId);
            }

            var weapon = WeaponConfig.Get(type);
            var armor = ArmorConfig.Get(type);
            var mask = NetComponents.Item | NetComponents.Owner | NetComponents.Attachment;
            if (weapon != null) mask |= NetComponents.Weapon;
            if (armor != null) mask |= NetComponents.Armor;

            var equipped = objects.Spawn(ObjectType.Item, mask, player.Position, obj =>
            {
                obj.Item = new ItemState { Type = type };
                obj.Owner = new OwnerState { PlayerId = player.Id };
                obj.Attachment = new AttachmentState { Slot = slot };
                if (weapon is { } w)
                    obj.Weapon = new WeaponState
                    {
                        CurrentAmmo = Math.Clamp(ammo ?? w.MagazineCapacity, 0, w.MagazineCapacity),
                        ReserveAmmo = ReserveFor(type, w),
                    };
                if (armor is { } a) obj.Armor = new ArmorState { MaxValue = a.Max, Current = a.Max };
            });
            player.Equipped[slot] = equipped.NetworkId;
            return equipped;
        }

        /// <summary>
        /// Equips the nearest valid pickup, swapping the occupied slot. Target selection is authoritative.
        /// </summary>
        /// <param name="actors">
        /// All actors, used to exclude objects currently operated by another actor.
        /// </param>
        public void ApplyInteract(ServerPlayer player, IEnumerable<ServerPlayer> actors)
        {
            // Hands full: E puts the thing down instead of looking for another one. One key, and it
            // reads the same way round every time — E is "change what is in my hands".
            if (player.IsCarrying)
            {
                PutDown(player);
                return;
            }

            // Find first, act after: Despawn/Spawn mutate the object dictionary
            // and must not run inside its enumeration.
            //
            // The choice itself is PickupTargeting's, in Common, because the client runs the same
            // one to decide what its "Press E" prompt should name — see that file.
            var pickup = PickupTargeting.Nearest(
                player.Position,
                objects.All.Where(candidate => !IsBeingWorked(candidate.NetworkId, actors)),
                Describe);
            if (pickup == null) return;

            TryTake(player, pickup, actors);
        }

        /// <summary>AI-targetable form of interact: same validation and transition, exact object.</summary>
        internal bool TryTake(
            ServerPlayer player,
            ServerObject pickup,
            IEnumerable<ServerPlayer> actors)
        {
            if (player.IsCarrying
                || IsBeingWorked(pickup.NetworkId, actors)
                || !PickupTargeting.IsAvailable(Describe(pickup))
                || Vector3.DistanceSquared(player.Position, pickup.Transform.Position)
                    > PickupTargeting.RadiusSquared)
                return false;

            // Something hauled goes into the hands, not into the kit: it must not take the rifle's
            // slot, because picking it up is not a swap. Whatever was already being carried IS
            // swapped out — you have one pair of hands.
            var slot = ItemConfig.IsCarryable(pickup.Item.Type)
                ? EquipSlot.Carried
                : pickup.Has.HasFlag(NetComponents.Weapon)
                    ? HotbarConfig.StorageSlot(HotbarConfig.SlotFor(pickup.Item.Type))
                    : ItemConfig.Get(pickup.Item.Type).Slot;
            Equip(player, pickup, slot);
            return true;
        }

        /// <summary>
        /// Places the carried item at the player with their yaw, which sets an emplacement's traverse axis.
        /// </summary>
        public void PutDown(ServerPlayer player)
        {
            if (!player.Equipped.Remove(EquipSlot.Carried, out uint carriedId)) return;
            if (!objects.TryGet(carriedId, out var carried)) return;

            // No deadline. Setting a mortar down is emplacing it, and an emplacement that dissolved
            // after a minute would be a weapon the map quietly took back off the player.
            Drop(carried, player.Position, player.Yaw, despawnAtTick: 0);
        }

        private void Equip(ServerPlayer player, ServerObject pickup, EquipSlot slot)
        {
            // Swap: the current occupant drops where the player stands, and starts rotting — a gun
            // he chose to put down for a better one is litter like any other.
            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
                Drop(current, player.Position, player.Yaw, LitterDeadline());

            // pickup -> equipped: despawn + respawn with Transform swapped for
            // Owner + Attachment. CopyComponents carries every shared bit — live
            // state (ammo) and any future trait ride along automatically.
            var mask = (pickup.Has & ~NetComponents.Transform) | NetComponents.Owner | NetComponents.Attachment;
            objects.Despawn(pickup.NetworkId);

            var equipped = objects.Spawn(ObjectType.Item, mask, player.Position, obj =>
            {
                ServerObject.CopyComponents(pickup, obj, pickup.Has & mask);
                obj.Owner = new OwnerState { PlayerId = player.Id };
                obj.Attachment = new AttachmentState { Slot = slot };
            });
            player.Equipped[slot] = equipped.NetworkId;
        }

        /// <summary>When something dropped right now stops being worth walking to.</summary>
        private uint LitterDeadline() => now + (uint)ItemConfig.DroppedLifetimeTicks;

        /// <summary>
        /// Returns an equipped item to the world, preserving <paramref name="yaw"/> for emplacement traverse.
        /// </summary>
        private void Drop(ServerObject equipped, Vector3 position, float yaw, uint despawnAtTick)
        {
            // equipped -> pickup: the mirror image of Equip's transition. The
            // spawn position IS the pickup's Transform, so it must not be copied
            // (equipped.Has has no Transform bit, so the shared mask excludes it).
            var mask = (equipped.Has & ~(NetComponents.Owner | NetComponents.Attachment)) | NetComponents.Transform;
            objects.Despawn(equipped.NetworkId);

            objects.Spawn(ObjectType.Item, mask, position, obj =>
            {
                ServerObject.CopyComponents(equipped, obj, equipped.Has & mask);
                obj.Transform.Yaw = yaw;
                obj.DespawnAtTick = despawnAtTick;
            });
        }

        /// <summary>
        /// Removes a one-use equipped item without dropping it. The expected network ID prevents a
        /// delayed use request from consuming whatever was subsequently swapped into the slot.
        /// </summary>
        public bool ConsumeEquipped(
            ServerPlayer player,
            EquipSlot slot,
            uint expectedNetworkId)
        {
            if (!player.Equipped.TryGetValue(slot, out uint equippedId)
                || equippedId != expectedNetworkId)
                return false;

            player.Equipped.Remove(slot);
            objects.Despawn(equippedId);
            return true;
        }

        /// <summary>
        /// Item selected by a hotbar slot. Primary falls back to Hand for legacy administrative items.
        /// </summary>
        public ItemType? HeldItem(ServerPlayer player, HotbarSlot hotbar)
        {
            if (!HotbarConfig.IsValid(hotbar)) return null;

            var slot = HotbarConfig.StorageSlot(hotbar);
            if (hotbar == HotbarSlot.Primary && !player.Equipped.ContainsKey(slot))
                slot = EquipSlot.Hand;

            return player.Equipped.TryGetValue(slot, out uint itemId) && objects.TryGet(itemId, out var item)
                ? item.Item.Type
                : null;
        }

        /// <summary>
        /// The movement multiplier for whatever is in the actor's selected slot, which is the
        /// server's half of <see cref="PlayerMovement.Step"/>'s speedScale. The client predicts the
        /// same number from its own map of that slot; see <see cref="ItemStats.MoveSpeedScale"/>
        /// for why only weapons are allowed to carry one.
        /// </summary>
        public float MoveSpeedScale(ServerPlayer player, HotbarSlot hotbar)
            => CarriedItem(player) is { } hauled ? ItemConfig.MoveSpeedScale(hauled)
             : HeldItem(player, hotbar) is { } type ? ItemConfig.MoveSpeedScale(type)
             : 1f;

        /// <summary>Whether anybody currently has this object as their emplacement.</summary>
        internal static bool IsBeingWorked(uint networkId, IEnumerable<ServerPlayer> actors)
        {
            foreach (var actor in actors)
                if (actor.OperatingObjectId == networkId)
                    return true;
            return false;
        }

        /// <summary>
        /// Nearest unoccupied emplacement in reach, using the same targeting rule as pickup interaction.
        /// </summary>
        public ServerObject? EmplacedInReach(ServerPlayer player, IEnumerable<ServerPlayer> actors)
        {
            var nearest = PickupTargeting.Nearest(
                player.Position,
                objects.All.Where(candidate =>
                    candidate.NetworkId == player.OperatingObjectId
                    || !IsBeingWorked(candidate.NetworkId, actors)),
                Describe);
            return nearest is not null && ItemConfig.IsCarryable(nearest.Item.Type) ? nearest : null;
        }

        /// <summary>What this player is hauling, or null. Its weight is what he moves at.</summary>
        public ItemType? CarriedItem(ServerPlayer player)
            => player.Equipped.TryGetValue(EquipSlot.Carried, out uint id)
               && objects.TryGet(id, out var item)
                ? item.Item.Type
                : null;

        /// <summary>
        /// Applies a hotbar selection unless the actor is carrying an item.
        /// </summary>
        public void SelectHotbar(ServerPlayer player, HotbarSlot hotbar)
        {
            if (player.IsCarrying) return;
            player.Hotbar = hotbar;
        }

        /// <summary>
        /// Drops the dead actor's current gun with its live ammunition state. Other kit remains equipped.
        /// </summary>
        public void DropOnDeath(ServerPlayer player)
        {
            // A grenade stack is not a weapon you pick up off the ground — it is ammunition, and one
            // dropped by every casualty would carpet a contested position in them. A carryable is
            // exempt for the opposite reason: it is emplaced, not held, and PutDown is how it moves.
            var dropped = player.Equipped
                .Where(pair => pair.Key != EquipSlot.Carried
                               && objects.TryGet(pair.Value, out var item)
                               && item.Has.HasFlag(NetComponents.Weapon)
                               && !ItemCatalog.HasBehavior(item.Item.Type, ItemBehavior.Grenade))
                .ToArray();

            // Collected first: Drop despawns and respawns objects, so it mutates what it iterates.
            foreach (var (slot, id) in dropped)
            {
                player.Equipped.Remove(slot);
                if (objects.TryGet(id, out var item))
                    Drop(item, player.Position, player.Yaw, LitterDeadline());
            }
        }

        /// <summary>Everything worn leaves with its owner. Call from RemovePlayer.
        /// Miss this and every client keeps orphan views retrying their attach
        /// forever.</summary>
        public void DespawnFor(ServerPlayer player)
        {
            foreach (uint networkId in player.Equipped.Values)
                objects.Despawn(networkId);
            player.Equipped.Clear();
        }
    }
}
