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
        /// <summary>
        /// Hauled rather than fought with. It is picked up and held exactly as an
        /// <see cref="Equippable"/> is — same slot, same swap-drops-the-occupant — and differs in
        /// one respect that the rest of the game reads off this category rather than off the item's
        /// identity: it CANNOT BE USED FROM YOUR HANDS. Whatever it does, it does once it is set
        /// down somewhere.
        ///
        /// The mortar is the first, and the reason the category exists: it fires from its
        /// emplacement, at a point, over an arc, and letting the ordinary fire path have it would
        /// produce a flat rifle shot out of a tube somebody is carrying. That rule is worth stating
        /// once about a KIND of thing rather than once per thing — an ammo crate, a heavy machine
        /// gun and a radio all want it, and none of them should have to be named in the weapon code
        /// to get it.
        /// </summary>
        Carryable,
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
        /// <summary>
        /// Held in BOTH hands, in front of you — what a <see cref="ItemCategory.Carryable"/> goes
        /// into. It is deliberately not a hotbar slot and does not displace one: picking up
        /// something heavy does not make you throw your rifle away, it makes your hands unavailable.
        /// Everything that asks "can this man use what he is equipped with" reads
        /// <see cref="ServerPlayer.IsCarrying"/>, which is this slot being occupied.
        /// </summary>
        Carried,
    }

    /// <summary>Identity-level facts every item has. Trait stats live in the
    /// per-trait tables (WeaponConfig, ArmorConfig, ...) so rows here stay
    /// dense and adding a trait never churns them. Never on the wire: both
    /// ends key into it by ItemState.Type.</summary>
    /// <param name="MoveSpeedScale">
    /// What carrying this does to <see cref="PlayerMovement.WalkSpeed"/> and every other speed the
    /// movement step picks — 1 for anything light enough not to notice.
    ///
    /// It is identity-level rather than a weapon statistic because weight is a property of the
    /// OBJECT, not of what it shoots: it used to live in <see cref="WeaponStats"/> on the grounds
    /// that guns were the only things heavy enough to matter, and the mortar is the counterexample
    /// that ends that. A carryable with no weapon row could not have been made heavy at all.
    ///
    /// Both ends read it through <see cref="ItemConfig.MoveSpeedScale"/>, and that matters: the
    /// client PREDICTS movement, so a weight the server applies and the prediction does not shows
    /// up as a reconciliation correction every tick the thing is in hand.
    /// </param>
    public readonly record struct ItemStats(
        ItemCategory Category,
        EquipSlot Slot,
        float MoveSpeedScale = 1f);

    public static class ItemConfig
    {
        /// <summary>
        /// The primary a human player receives on spawn. NPCs have their own default because their
        /// four-man squad composition deliberately mixes two assault guns with two rifles.
        /// </summary>
        public const ItemType DefaultPlayerPrimaryWeapon = ItemType.Mosin;

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
            ItemType.Dp27 => new(ItemCategory.Equippable, EquipSlot.Hand, MoveSpeedScale: 0.7f),
            // Carryable, not Equippable: picking one up costs you the rifle in your hands, and the
            // category is what stops it being fired from them.
            ItemType.Mortar => new(
                ItemCategory.Carryable,
                EquipSlot.Hand,
                MoveSpeedScale: MortarConfig.CarryMoveSpeedScale),
            ItemType.BodyArmor => new(ItemCategory.Equippable, EquipSlot.Chest),
            ItemType.Grenade => new(ItemCategory.Equippable, EquipSlot.Hand),
            // The shovel is a tool, not a gun: it has no WeaponConfig row, so it never gets a
            // WeaponState bit and fire/reload never apply to it. Its slot is hotbar 2, the one
            // TerrainSystem already reads as "authorized to dig".
            ItemType.Shovel => new(ItemCategory.Equippable, EquipSlot.HotbarShovel),

            // Unknown type off the wire: a bare hand equippable rather than a crash.
            _ => new(ItemCategory.Equippable, EquipSlot.Hand),
        };

        /// <summary>
        /// Whether E takes this into your hands at all — things you fight with and things you haul
        /// alike. The pickup path, the equip path and the "press E" prompt all ask this rather than
        /// testing for one category, so adding a third held kind does not mean finding them again.
        /// </summary>
        public static bool IsHeld(ItemType type)
            => Get(type).Category is ItemCategory.Equippable or ItemCategory.Carryable;

        /// <summary>
        /// Whether this is hauled rather than fought with: it fills your hands and cannot be used
        /// from them. See <see cref="ItemCategory.Carryable"/>.
        /// </summary>
        public static bool IsCarryable(ItemType type)
            => Get(type).Category == ItemCategory.Carryable;

        /// <summary>What carrying this does to movement speed; 1 for anything light.</summary>
        public static float MoveSpeedScale(ItemType type) => Get(type).MoveSpeedScale;
    }
}
