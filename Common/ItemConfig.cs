namespace Demiurge;

public enum ItemCategory : byte
{
    Equippable,
    Item,
    Carryable,
}

/// <summary>Where an equippable sits. This remains an engine concept because attachment state and
/// player skeleton sockets are protocol/layout, while which slot an item uses is datapack data.</summary>
public enum EquipSlot : byte
{
    Hand,
    Chest,
    Head,
    Back,
    HotbarPrimary,
    HotbarGrenade,
    HotbarShovel,
    Carried,
}

public readonly record struct ItemStats(
    ItemCategory Category,
    EquipSlot Slot,
    float MoveSpeedScale = 1f);

public static class ItemConfig
{
    /// <summary>
    /// How long a DROPPED item survives on the ground. Long enough to walk to a man you just shot
    /// and take his rifle, short enough that a contested position does not silently accumulate a
    /// hundred replicated objects over a long match.
    ///
    /// It applies to litter only — something that fell out of a dead or swapping man's hands. A
    /// pickup placed in the world by a map, an editor placement or a spawn command lasts forever,
    /// and so does a carryable deliberately set down, because those are decisions about the world
    /// rather than debris from a fight. See <c>ItemSystem.Drop</c>.
    /// </summary>
    public const float DroppedLifetimeSeconds = 60f;

    public static int DroppedLifetimeTicks => (int)(DroppedLifetimeSeconds * NetworkConfig.TickRate);

    /// <summary>
    /// What a man carries in his POUCHES, counted in magazines rather than rounds so it means the
    /// same thing for a 10-round carbine and a 71-round drum. The one already in the weapon is on
    /// top of this, so a rifleman spawns with six magazines of ammunition in total and can reload
    /// five times.
    /// </summary>
    public const int SpareMagazines = 5;

    public static ItemType DefaultPlayerPrimaryWeapon => ItemCatalog.Registry.DefaultPlayerPrimary;
    public static ItemType DefaultNpcPrimaryWeapon => ItemCatalog.Registry.DefaultNpcPrimary;
    public static ItemType UnidentifiedThreatWeapon => ItemCatalog.Registry.UnidentifiedThreatWeapon;
    public static ItemType DefaultAssaultWeapon => ItemCatalog.Registry.DefaultAssaultWeapon;
    public static ItemType DefaultMarksmanWeapon => ItemCatalog.Registry.DefaultMarksmanWeapon;

    public static ItemStats Get(ItemType type)
        => ItemCatalog.TryGet(type)?.Stats
           ?? new ItemStats(ItemCategory.Equippable, EquipSlot.Hand);

    public static bool IsHeld(ItemType type)
        => Get(type).Category is ItemCategory.Equippable or ItemCategory.Carryable;

    public static bool IsCarryable(ItemType type)
        => Get(type).Category == ItemCategory.Carryable;

    public static float MoveSpeedScale(ItemType type) => Get(type).MoveSpeedScale;
}
