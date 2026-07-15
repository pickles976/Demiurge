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
            // The config's sections are the mask recipe: a weapon section means
            // a WeaponState bit, an armor section an ArmorState bit, and so on.
            var stats = ItemConfig.Get(type);
            var mask = NetComponents.Item | NetComponents.Transform;
            if (stats.Weapon != null) mask |= NetComponents.Weapon;
            if (stats.Armor != null) mask |= NetComponents.Armor;

            return objects.Spawn(ObjectType.Item, mask, position, obj =>
            {
                obj.Item = new ItemState { Type = type };
                if (stats.Weapon is { } w) obj.Weapon = new WeaponState { CurrentAmmo = w.MagazineCapacity };
                if (stats.Armor is { } a) obj.Armor = new ArmorState { MaxValue = a, Current = a };
            });
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

            Equip(player, pickup, stats.Slot);
        }

        private void Equip(ServerPlayer player, ServerObject pickup, EquipSlot slot)
        {
            // Swap: the current occupant drops where the player stands.
            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
                Drop(current, player.Position);

            // pickup -> equipped: despawn + respawn with Transform swapped for
            // Owner. Live state (ammo) rides the carried structs.
            var carried = (pickup.Item, pickup.Weapon, pickup.Armor);
            var mask = (pickup.Has & ~NetComponents.Transform) | NetComponents.Owner;
            objects.Despawn(pickup.NetworkId);

            var equipped = objects.Spawn(ObjectType.Item, mask, player.Position, obj =>
            {
                (obj.Item, obj.Weapon, obj.Armor) = carried;
                obj.Owner = new OwnerState { PlayerId = player.Id };
            });
            player.Equipped[slot] = equipped.NetworkId;
        }

        private void Drop(ServerObject equipped, Vector3 position)
        {
            // equipped -> pickup: the mirror image of Equip's transition.
            var carried = (equipped.Item, equipped.Weapon, equipped.Armor);
            var mask = (equipped.Has & ~NetComponents.Owner) | NetComponents.Transform;
            objects.Despawn(equipped.NetworkId);

            objects.Spawn(ObjectType.Item, mask, position,
                obj => (obj.Item, obj.Weapon, obj.Armor) = carried);
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
