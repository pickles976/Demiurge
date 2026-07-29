namespace Demiurge;

/// <summary>Stable player-facing hotbar positions. Values match their number keys.</summary>
public enum HotbarSlot : byte
{
    Primary = 1,
    Shovel = 2,
    Grenade = 3,
}

public static class HotbarConfig
{
    public const int SlotCount = 3;

    public static bool IsValid(HotbarSlot slot)
        => slot is >= HotbarSlot.Primary and <= HotbarSlot.Grenade;

    public static HotbarSlot Scroll(HotbarSlot current, int direction)
    {
        int zeroBased = (int)current - 1;
        int wrapped = (zeroBased + direction + SlotCount) % SlotCount;
        return (HotbarSlot)(wrapped + 1);
    }

    public static EquipSlot StorageSlot(HotbarSlot slot) => slot switch
    {
        HotbarSlot.Primary => EquipSlot.HotbarPrimary,
        HotbarSlot.Grenade => EquipSlot.HotbarGrenade,
        _ => throw new InvalidOperationException($"{slot} has no stored item"),
    };

    public static bool TryFromStorageSlot(EquipSlot slot, out HotbarSlot hotbar)
    {
        hotbar = slot switch
        {
            EquipSlot.HotbarPrimary => HotbarSlot.Primary,
            EquipSlot.HotbarGrenade => HotbarSlot.Grenade,
            _ => default,
        };
        return hotbar != default;
    }

    public static HotbarSlot SlotFor(ItemType type)
        => type == ItemType.Grenade ? HotbarSlot.Grenade : HotbarSlot.Primary;
}
