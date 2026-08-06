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
    /// keyed by this enum and nothing enumerates it. A new slot is this line
    /// plus a socket row in ItemCosmetics (where it sits on the body).
    /// Rides the wire inside AttachmentState — append-only.</summary>
    public enum EquipSlot : byte
    {
        Hand,
        Chest,
        Head,
        Back,
        HotbarPrimary,
        HotbarGrenade,
        HotbarShovel,
    }

    /// <summary>Identity-level facts every item has. Trait stats live in the
    /// per-trait tables (WeaponConfig, ArmorConfig, ...) so rows here stay
    /// dense and adding a trait never churns them. Never on the wire: both
    /// ends key into it by ItemState.Type.</summary>
    public readonly record struct ItemStats(
        ItemCategory Category,
        EquipSlot Slot);

    public static class ItemConfig
    {
        /// <summary>
        /// The primary a human player receives on spawn. NPCs have their own default because their
        /// four-man squad composition deliberately mixes two assault guns with two rifles.
        /// </summary>
        public const ItemType DefaultPlayerPrimaryWeapon = ItemType.Dp27;

        /// <summary>The intermediate-range half of a default NPC squad.</summary>
        public const ItemType DefaultNpcPrimaryWeapon = ItemType.Sks;

        public static ItemStats Get(ItemType type) => type switch
        {
            ItemType.Ak47 => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.AWP => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.Glock => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.Sks => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.Ppsh => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.Mosin => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.Dp27 => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.BodyArmor => new(ItemCategory.Equippable, EquipSlot.Chest),
            ItemType.Grenade => new(ItemCategory.Equippable, EquipSlot.Hand),
            // The shovel is a tool, not a gun: it has no WeaponConfig row, so it never gets a
            // WeaponState bit and fire/reload never apply to it. Its slot is hotbar 2, the one
            // TerrainSystem already reads as "authorized to dig".
            ItemType.Shovel => new(ItemCategory.Equippable, EquipSlot.HotbarShovel),

            // Unknown type off the wire: a bare hand equippable rather than a crash.
            _ => new(ItemCategory.Equippable, EquipSlot.Hand),
        };
    }
}
