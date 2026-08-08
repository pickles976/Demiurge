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
