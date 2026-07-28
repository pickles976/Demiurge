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
        public static ItemStats Get(ItemType type) => type switch
        {
            ItemType.Ak47 => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.AWP => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.Glock => new(ItemCategory.Equippable, EquipSlot.Hand),
            ItemType.BodyArmor => new(ItemCategory.Equippable, EquipSlot.Chest),

            // Unknown type off the wire: a bare hand equippable rather than a crash.
            _ => new(ItemCategory.Equippable, EquipSlot.Hand),
        };
    }
}
