namespace Demiurge;

/// <summary>A behavior implemented by the engine that an item definition may opt into. Data can
/// compose supported behavior; genuinely new mechanics still require a new engine behavior.</summary>
public enum ItemBehavior
{
    None,
    Firearm,
    Grenade,
    Shovel,
    Mortar,
}

public readonly record struct VisualRecoilDefinition(float Back, float Lift, float PitchDegrees);

public sealed record BoltPresentationDefinition(
    float DelaySeconds,
    float TravelSeconds,
    float HoldSeconds,
    float ReturnSeconds,
    string? SoundPath);

/// <summary>View-only data kept in plain CLR types so Common can validate it without depending on
/// Stride. The client converts colors and rotations at its boundary.</summary>
public sealed record ItemPresentationDefinition(
    string Model,
    float WorldScale,
    float AimMagnification,
    float AimSpeedScale,
    IReadOnlyList<string> ShotSounds,
    string TracerColor,
    string? ReloadSound,
    string? DistantReportSound,
    float ReloadVolume,
    float CameraTrauma,
    VisualRecoilDefinition VisualRecoil,
    float AdsRecoilScale,
    BoltPresentationDefinition? Bolt);

/// <summary>The resolved result of all active datapacks for one namespaced item ID.</summary>
public sealed record ItemDefinition(
    ItemType Type,
    string Id,
    string Name,
    IReadOnlyList<string> Aliases,
    ItemStats Stats,
    ItemBehavior Behavior,
    HotbarSlot? Hotbar,
    WeaponStats? Weapon,
    ArmorStats? Armor,
    ItemPresentationDefinition Presentation);

/// <summary>Compatibility facade over the immutable datapack registry. Existing systems keep their
/// compact ItemType keys while definitions, traits, aliases, and presentation come from JSON.</summary>
public static class ItemCatalog
{
    public static DataPackRegistry Registry { get; } = DataPackLoader.LoadDefault();

    public static IReadOnlyList<ItemDefinition> All => Registry.Items;

    public static bool TryResolve(string name, out ItemType type)
        => Registry.TryResolve(name, out type);

    public static ItemDefinition Get(ItemType type) => Registry.RequireItem(type);

    public static ItemDefinition? TryGet(ItemType type) => Registry.GetItem(type);

    public static string Id(ItemType type) => Get(type).Id;

    public static string Name(ItemType type) => Get(type).Name;

    public static string DebugName(ItemType type)
        => TryGet(type)?.Id ?? $"unknown item handle {(ushort)type}";

    public static bool HasBehavior(ItemType type, ItemBehavior behavior)
        => TryGet(type)?.Behavior == behavior;

    public static ItemType RequireBehavior(ItemBehavior behavior)
        => All.SingleOrDefault(item => item.Behavior == behavior)?.Type
           ?? throw new InvalidOperationException($"No active datapack item provides {behavior}");
}
