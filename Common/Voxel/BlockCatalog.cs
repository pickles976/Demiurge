namespace Demiurge;

public readonly record struct BlockDefinition(
    BlockType Type,
    string Id,
    IReadOnlyList<string> Aliases);

/// <summary>Stable source-file and command identity for voxel materials.</summary>
public static class BlockCatalog
{
    private static readonly BlockDefinition[] definitions =
    [
        new(BlockType.BlockType_Grass, "demiurge:grass", ["grass"]),
        new(BlockType.BlockType_Dirt, "demiurge:dirt", ["dirt"]),
        new(BlockType.BlockType_Stone, "demiurge:stone", ["stone", "rock"]),
    ];

    private static readonly Dictionary<string, BlockType> byName = BuildLookup();
    private static readonly Dictionary<BlockType, BlockDefinition> byType =
        definitions.ToDictionary(definition => definition.Type);

    public static IReadOnlyList<BlockDefinition> All => definitions;

    public static bool TryResolve(string name, out BlockType type)
        => byName.TryGetValue(name, out type);

    public static BlockDefinition Get(BlockType type)
        => byType.TryGetValue(type, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown solid block type");

    public static string Id(BlockType type) => Get(type).Id;

    private static Dictionary<string, BlockType> BuildLookup()
    {
        var lookup = new Dictionary<string, BlockType>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            lookup.Add(definition.Id, definition.Type);
            foreach (string alias in definition.Aliases)
                lookup.Add(alias, definition.Type);
        }
        return lookup;
    }
}
