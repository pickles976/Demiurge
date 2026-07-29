using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>Pickups, equipping, swapping, dropping — for every item kind.
    /// Owns no storage: items live in ObjectReplication as Item-masked objects,
    /// slot occupancy lives on ServerPlayer.Equipped, and what an item IS comes
    /// from ItemConfig. Transitions only move mask bits around:
    /// pickup = Item|Transform(+traits), equipped = Item|Owner(+traits) — every
    /// trait bit (Weapon, Armor, future ones) carries across untouched.</summary>
    public class ItemSystem
    {
        private readonly ObjectReplication objects;

        private const float PickupRadiusSq = 0.75f * 0.75f;

        public ItemSystem(ObjectReplication objects) => this.objects = objects;

        public ServerObject SpawnPickup(ItemType type, Vector3 position)
        {
            // The trait tables are the mask recipe: a WeaponConfig row means a
            // WeaponState bit, an ArmorConfig row an ArmorState bit, and so on.
            var weapon = WeaponConfig.Get(type);
            var armor = ArmorConfig.Get(type);
            var mask = NetComponents.Item | NetComponents.Transform;
            if (weapon != null) mask |= NetComponents.Weapon;
            if (armor != null) mask |= NetComponents.Armor;

            return objects.Spawn(ObjectType.Item, mask, position, obj =>
            {
                obj.Item = new ItemState { Type = type };
                if (weapon is { } w) obj.Weapon = new WeaponState { CurrentAmmo = w.MagazineCapacity };
                if (armor is { } a) obj.Armor = new ArmorState { MaxValue = a.Max, Current = a.Max };
            });
        }

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

        private ServerObject SpawnOwned(
            ServerPlayer player,
            ItemType type,
            EquipSlot slot,
            int? ammo,
            bool dropReplaced)
        {
            var stats = ItemConfig.Get(type);
            if (stats.Category != ItemCategory.Equippable)
                throw new InvalidOperationException($"{type} cannot be equipped");

            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
            {
                if (dropReplaced)
                    Drop(current, player.Position);
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
                    };
                if (armor is { } a) obj.Armor = new ArmorState { MaxValue = a.Max, Current = a.Max };
            });
            player.Equipped[slot] = equipped.NetworkId;
            return equipped;
        }

        /// <summary>E pressed: equip the nearest pickup in radius, swapping out
        /// whatever occupies its slot. Server-authoritative — the client sends
        /// no target, so there is nothing to validate beyond proximity.</summary>
        public void ApplyInteract(ServerPlayer player)
        {
            // Find first, act after: Despawn/Spawn mutate the object dictionary
            // and must not run inside its enumeration.
            ServerObject? pickup = null;
            float bestSq = PickupRadiusSq;
            foreach (var obj in objects.All)
            {
                if (!obj.Has.HasFlag(NetComponents.Item | NetComponents.Transform)) continue;
                float dSq = Vector3.DistanceSquared(obj.Transform.Position, player.Position);
                if (dSq > bestSq) continue;
                pickup = obj;
                bestSq = dSq;
            }
            if (pickup == null) return;

            var stats = ItemConfig.Get(pickup.Item.Type);
            if (stats.Category != ItemCategory.Equippable) return;   // walk-over inventory items: future

            var slot = !player.IsMob && pickup.Has.HasFlag(NetComponents.Weapon)
                ? HotbarConfig.StorageSlot(HotbarConfig.SlotFor(pickup.Item.Type))
                : stats.Slot;
            Equip(player, pickup, slot);
        }

        private void Equip(ServerPlayer player, ServerObject pickup, EquipSlot slot)
        {
            // Swap: the current occupant drops where the player stands.
            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
                Drop(current, player.Position);

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

        private void Drop(ServerObject equipped, Vector3 position)
        {
            // equipped -> pickup: the mirror image of Equip's transition. The
            // spawn position IS the pickup's Transform, so it must not be copied
            // (equipped.Has has no Transform bit, so the shared mask excludes it).
            var mask = (equipped.Has & ~(NetComponents.Owner | NetComponents.Attachment)) | NetComponents.Transform;
            objects.Despawn(equipped.NetworkId);

            objects.Spawn(ObjectType.Item, mask, position,
                obj => ServerObject.CopyComponents(equipped, obj, equipped.Has & mask));
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
