namespace Demiurge
{
    public enum ItemCategory : byte
    {
        /// <summary>Worn or held in an EquipSlot; picked up with E, swapping
        /// drops the current occupant back into the world.</summary>
        Equippable,
        /// <summary>Walk-over loot that goes into an inventory. Reserved —
        /// nothing spawns these yet; ApplyInteract ignores them.</summary>
        Item,
    }

    /// <summary>Where an equippable sits. Occupancy is a per-player dictionary
    /// keyed by this enum and nothing enumerates it — a new slot is one line.
    /// WHERE a slot sits on the body is each item's ItemCosmetics entry.</summary>
    public enum EquipSlot : byte
    {
        Hand,
        Chest,
        Head,
        Back,
    }

    /// <summary>Static per-item data. Never on the wire: both ends key into it
    /// by ItemState.Type — the same client-predicts/server-enforces contract as
    /// PlayerMovement. The Weapon/Armor sections double as the mask recipe:
    /// ItemSystem.SpawnPickup adds the WeaponState/ArmorState bits iff the
    /// section is present.</summary>
    public readonly record struct ItemStats(
        ItemCategory Category,
        EquipSlot Slot,
        WeaponStats? Weapon = null,
        float? Armor = null);

    public static class ItemConfig
    {
        public static ItemStats Get(ItemType type) => type switch
        {
            ItemType.Ak47 => new(ItemCategory.Equippable, EquipSlot.Hand,
                new WeaponStats(MagazineCapacity: 30, TicksPerShot: 3, ReloadTicks: 45, Damage: 10, MaxRange: 100f, shiftNear: 2.5f, shiftFar: 6.0f)),
            ItemType.AWP => new(ItemCategory.Equippable, EquipSlot.Hand,
                new WeaponStats(MagazineCapacity: 5, TicksPerShot: 60, ReloadTicks: 45, Damage: 75, MaxRange: 200f, shiftNear: 2.5f, shiftFar: 12.5f)),
            ItemType.Glock => new(ItemCategory.Equippable, EquipSlot.Hand,
                new WeaponStats(MagazineCapacity: 15, TicksPerShot: 7, ReloadTicks: 20, Damage: 5, MaxRange: 50f, shiftNear: 1.5f, shiftFar: 2.5f)),
            ItemType.BodyArmor => new(ItemCategory.Equippable, EquipSlot.Chest, Armor: 50f),

            // Unknown type off the wire: AK stand-in rather than a crash.
            _ => new(ItemCategory.Equippable, EquipSlot.Hand, new WeaponStats(30, 3, 45, 10, 100f, 1.5f, 2.5f)),
        };

        /// <summary>The weapon section, or the AK fallback for call sites that
        /// know they hold a gun (fire/reload gates on the WeaponState bit first).</summary>
        public static WeaponStats GetWeapon(ItemType type) =>
            Get(type).Weapon ?? new WeaponStats(30, 3, 45, 10, 100f, 1.5f, 2.5f);
    }
}
