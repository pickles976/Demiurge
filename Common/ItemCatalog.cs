namespace Demiurge;

/// <summary>
/// Stable external identity for an item. <see cref="ItemType"/> remains the compact wire value;
/// command text and future data files use the canonical names here instead of C# enum spellings.
/// </summary>
public readonly record struct ItemDefinition(ItemType Type, string Id, IReadOnlyList<string> Aliases);

public static class ItemCatalog
{
    private static readonly ItemDefinition[] definitions =
    [
        new(ItemType.Ak47, "demiurge:ak47", ["ak47", "ak-47", "ak"]),
        new(ItemType.Sks, "demiurge:sks", ["sks"]),
        new(ItemType.Shovel, "demiurge:shovel", ["shovel", "spade"]),
        new(ItemType.AWP, "demiurge:awp", ["awp"]),
        new(ItemType.Glock, "demiurge:glock", ["glock"]),
        new(ItemType.BodyArmor, "demiurge:body_armor", ["body_armor", "body-armor", "bodyarmor"]),
        new(ItemType.Grenade, "demiurge:grenade", ["grenade"]),
    ];

    private static readonly Dictionary<string, ItemType> byName = BuildLookup();
    private static readonly Dictionary<ItemType, ItemDefinition> byType =
        definitions.ToDictionary(definition => definition.Type);

    public static IReadOnlyList<ItemDefinition> All => definitions;

    public static bool TryResolve(string name, out ItemType type)
        => byName.TryGetValue(name, out type);

    public static ItemDefinition Get(ItemType type)
        => byType.TryGetValue(type, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown item type");

    public static string Id(ItemType type) => Get(type).Id;

    private static Dictionary<string, ItemType> BuildLookup()
    {
        var lookup = new Dictionary<string, ItemType>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            lookup.Add(definition.Id, definition.Type);
            foreach (string alias in definition.Aliases)
                lookup.Add(alias, definition.Type);
        }
        return lookup;
    }
}
