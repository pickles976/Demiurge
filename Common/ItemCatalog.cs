namespace Demiurge;

/// <summary>
/// Stable external identity for an item. <see cref="ItemType"/> remains the compact wire value;
/// command text and future data files use the canonical names here instead of C# enum spellings.
///
/// Three names, and they point in different directions: <paramref name="Id"/> is what the game
/// calls it to itself, <paramref name="Aliases"/> is what a person may type at it, and
/// <paramref name="Name"/> is what it says back to a player. Only the last is allowed to be pretty.
/// </summary>
public readonly record struct ItemDefinition(
    ItemType Type,
    string Id,
    string Name,
    IReadOnlyList<string> Aliases);

public static class ItemCatalog
{
    private static readonly ItemDefinition[] definitions =
    [
        new(ItemType.Ak47, "demiurge:ak47", "AK-47", ["ak47", "ak-47", "ak"]),
        new(ItemType.Sks, "demiurge:sks", "SKS", ["sks"]),
        new(ItemType.Ppsh, "demiurge:ppsh", "PPSh-41", ["ppsh", "ppsh-41", "ppsh41"]),
        new(ItemType.Shovel, "demiurge:shovel", "Shovel", ["shovel", "spade"]),
        new(ItemType.AWP, "demiurge:awp", "AWP", ["awp"]),
        new(ItemType.Glock, "demiurge:glock", "Glock", ["glock"]),
        new(ItemType.BodyArmor, "demiurge:body_armor", "Body Armor", ["body_armor", "body-armor", "bodyarmor"]),
        new(ItemType.Grenade, "demiurge:grenade", "Grenade", ["grenade"]),
        new(ItemType.Mosin, "demiurge:mosin", "Mosin-Nagant", ["mosin", "mosin-nagant", "m9130"]),
        new(ItemType.Dp27, "demiurge:dp27", "DP-27", ["dp27", "dp-27", "dp_27", "dp"]),
        new(ItemType.Mortar, "demiurge:mortar", "Mortar", ["mortar"]),
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

    /// <summary>What to call this item in front of a player — a pickup prompt, a kill feed.</summary>
    public static string Name(ItemType type) => Get(type).Name;

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
