using System.Security.Cryptography;
using System.Text.Json;

namespace Demiurge.Editor;

public sealed record StructureBlock(Int3 Offset, string BlockId);

public sealed record StructureDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Name { get; init; }
    public Int3 Pivot { get; init; }
    public List<StructureBlock> Blocks { get; init; } = [];
    public string ContentHash { get; init; } = string.Empty;
}

public static class StructureLibrary
{
    private static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Captures every block given, bounded by where they actually are.
    ///
    /// There is no region to select. This runs against a structure world, whose whole content is
    /// what you built — so a box drawn around it can only ever be the box the blocks already
    /// describe, and asking for it twice is asking the user to restate something the document
    /// knows. The corners used to be set by hand and were the most confusing part of the feature.
    ///
    /// The pivot is the base of the bounding box at its horizontal centre. That is the anchor a
    /// placement is positioned by, and centring it is what makes <see cref="Transform"/> rotate the
    /// structure IN PLACE rather than swinging it around a corner and off the cursor.
    /// </summary>
    public static StructureDocument Capture(string name, IEnumerable<EditorBlockPlacement> blocks)
    {
        MapPathResolver.ValidateName(name);
        var cells = blocks as IReadOnlyCollection<EditorBlockPlacement> ?? blocks.ToArray();
        if (cells.Count == 0)
            throw new ArgumentException("There are no blocks to capture", nameof(blocks));

        int minX = cells.Min(block => block.Cell.X);
        int minY = cells.Min(block => block.Cell.Y);
        int minZ = cells.Min(block => block.Cell.Z);
        int maxX = cells.Max(block => block.Cell.X);
        int maxZ = cells.Max(block => block.Cell.Z);

        var pivot = new Int3((minX + maxX) / 2, minY, (minZ + maxZ) / 2);
        var captured = cells
            .Select(block => new StructureBlock(
                new Int3(block.Cell.X - pivot.X, block.Cell.Y - pivot.Y, block.Cell.Z - pivot.Z),
                block.BlockId))
            .OrderBy(block => block.Offset.Y)
            .ThenBy(block => block.Offset.Z)
            .ThenBy(block => block.Offset.X)
            .ToList();

        var document = new StructureDocument { Name = name, Pivot = pivot, Blocks = captured };
        return document with { ContentHash = Hash(document) };
    }

    public static void Save(string path, StructureDocument structure)
    {
        Validate(structure);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(structure, options));
        File.Move(temporary, fullPath, overwrite: true);
    }

    public static StructureDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        var structure = JsonSerializer.Deserialize<StructureDocument>(stream, options)
            ?? throw new InvalidDataException("Structure is empty");
        Validate(structure);
        if (!string.Equals(structure.ContentHash, Hash(structure with { ContentHash = string.Empty }), StringComparison.Ordinal))
            throw new InvalidDataException("Structure content hash does not match");
        return structure;
    }

    public static SetBlocksCommand CreatePlacementCommand(
        StructureDocument structure,
        Int3 anchor,
        int quarterTurns,
        bool mirrorX,
        EditorSession session)
    {
        Validate(structure);
        Guid group = Guid.NewGuid();
        var before = new Dictionary<Int3, EditorBlockPlacement?>();
        var after = new Dictionary<Int3, EditorBlockPlacement?>();

        foreach (var entry in structure.Blocks)
        {
            Int3 offset = Transform(entry.Offset, quarterTurns, mirrorX);
            var cell = new Int3(anchor.X + offset.X, anchor.Y + offset.Y, anchor.Z + offset.Z);
            before[cell] = session.BlockAt(cell);
            after[cell] = new EditorBlockPlacement
            {
                Id = Guid.NewGuid(),
                Sequence = session.AllocateSequence(),
                Cell = cell,
                BlockId = entry.BlockId,
                GroupId = group,
            };
        }

        return new SetBlocksCommand($"Place structure {structure.Name}", before, after);
    }

    public static Int3 Transform(Int3 offset, int quarterTurns, bool mirrorX)
    {
        int x = mirrorX ? -offset.X : offset.X;
        int z = offset.Z;
        int turns = ((quarterTurns % 4) + 4) % 4;
        return turns switch
        {
            0 => new Int3(x, offset.Y, z),
            1 => new Int3(-z, offset.Y, x),
            2 => new Int3(-x, offset.Y, -z),
            3 => new Int3(z, offset.Y, -x),
            _ => throw new InvalidOperationException("Quarter-turn normalization failed"),
        };
    }

    private static void Validate(StructureDocument structure)
    {
        if (structure.SchemaVersion != StructureDocument.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported structure schema {structure.SchemaVersion}");
        MapPathResolver.ValidateName(structure.Name);
        if (structure.Blocks.Count == 0) throw new InvalidDataException("Structure has no blocks");
        var cells = new HashSet<Int3>();
        foreach (var block in structure.Blocks)
        {
            if (!cells.Add(block.Offset)) throw new InvalidDataException($"Duplicate structure cell {block.Offset}");
            if (!BlockCatalog.TryResolve(block.BlockId, out _))
                throw new InvalidDataException($"Unknown structure material {block.BlockId}");
        }
    }

    private static string Hash(StructureDocument structure)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(structure with { ContentHash = string.Empty }, options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
