using System.Globalization;
using System.Numerics;

namespace Demiurge.Editor;

/// <summary>
/// Which tool the mouse is holding. Structures are their own mode rather than a state block mode
/// can be in: a selected structure took over left-click entirely and disabled the brush-size wheel,
/// so "block mode" already meant two different tools depending on a field somewhere else.
/// </summary>
public enum EditorToolMode { Terrain, Block, Object, Structure }
public enum EditorObjectChoiceKind { None, Pickup, Mob, Spawn, ConquestFlag, Crate, Tree, TreeBrush }

public sealed record EditorToolSettings
{
    public const int MaxBlockBrushDimension = 64;
    public const int MaxBlockBrushCells = 4096;

    public EditorToolMode Mode { get; set; } = EditorToolMode.Terrain;
    public EditMode TerrainMode { get; set; } = EditMode.Subtract;
    public EditShape TerrainShape { get; set; } = EditShape.Sphere;
    public Vector3 TerrainHalfExtent { get; set; } = new(1.5f);
    public float TerrainStrength { get; set; } = 1f;
    public BlockType TerrainMaterial { get; set; } = BlockType.BlockType_Grass;
    public BlockType Block { get; set; } = BlockType.BlockType_Stone;
    public Int3 BlockSize { get; set; } = new(1, 1, 1);
    public EditorObjectChoiceKind ObjectKind { get; set; }
    public string? ObjectId { get; set; }
    public float ObjectYaw { get; set; }
    public int ObjectTeam { get; set; } = 1;
    public TreeBrushSettings TreeBrush { get; init; } = new();
}

public readonly record struct EditorCommandResult(bool Success, string Output)
{
    public static EditorCommandResult Ok(string output) => new(true, output);
    public static EditorCommandResult Fail(string output) => new(false, output);
}

public static class EditorCommandParser
{
    public static EditorCommandResult Execute(
        string input,
        EditorToolSettings settings,
        EditorSession session)
    {
        string[] tokens = input.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens[0].Equals("editor", StringComparison.OrdinalIgnoreCase))
            tokens = tokens[1..];
        if (tokens.Length == 0) return EditorCommandResult.Fail("Usage: editor <status|mode|terrain|block|object|rotate|undo|redo>");

        try
        {
            return tokens[0].ToLowerInvariant() switch
            {
                "status" => EditorCommandResult.Ok(Status(settings, session)),
                "mode" => SetMode(tokens, settings),
                "terrain" => SetTerrain(tokens, settings),
                "block" => SetBlock(tokens, settings),
                "object" => SetObject(tokens, settings),
                "grove" => SetGrove(tokens, settings),
                "rotate" => Rotate(tokens, settings),
                "undo" => EditorCommandResult.Ok(session.Undo() is null ? "Nothing to undo" : "Undid editor action"),
                "redo" => EditorCommandResult.Ok(session.Redo() is null ? "Nothing to redo" : "Redid editor action"),
                _ => EditorCommandResult.Fail($"Unknown editor command: {tokens[0]}"),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return EditorCommandResult.Fail(ex.Message);
        }
    }

    private static EditorCommandResult SetMode(string[] tokens, EditorToolSettings settings)
    {
        if (tokens.Length != 2 || !Enum.TryParse<EditorToolMode>(tokens[1], true, out var mode))
            return EditorCommandResult.Fail("Usage: editor mode <terrain|block|object|structure>");
        settings.Mode = mode;
        return EditorCommandResult.Ok($"Editor mode: {mode.ToString().ToLowerInvariant()}");
    }

    private static EditorCommandResult SetGrove(string[] tokens, EditorToolSettings settings)
    {
        if (tokens.Length != 3)
            return EditorCommandResult.Fail(
                "Usage: editor grove <radius|spacing|density|terrain> <value>");

        var brush = settings.TreeBrush;
        switch (tokens[1].ToLowerInvariant())
        {
            case "radius":
                if (!Number(tokens[2], out float radius)
                    || radius < TreeBrushSettings.MinRadius
                    || radius > TreeBrushSettings.MaxRadius)
                    return EditorCommandResult.Fail(
                        $"Usage: editor grove radius <{TreeBrushSettings.MinRadius}..{TreeBrushSettings.MaxRadius}>");
                brush.Radius = radius;
                break;
            case "spacing":
                if (!Number(tokens[2], out float spacing)
                    || spacing < TreeScatter.MinimumSpacing
                    || spacing > TreeScatter.MaximumSpacing)
                    return EditorCommandResult.Fail(
                        $"Usage: editor grove spacing <{TreeScatter.MinimumSpacing}..{TreeScatter.MaximumSpacing}>");
                brush.Spacing = spacing;
                break;
            case "density":
                if (!Number(tokens[2], out float density) || density is < 0f or > 1f)
                    return EditorCommandResult.Fail("Usage: editor grove density <0..1>");
                brush.Density = density;
                break;
            case "terrain":
                brush.RespectTerrain = tokens[2].ToLowerInvariant() switch
                {
                    "on" => true,
                    "off" => false,
                    _ => throw new ArgumentException("Usage: editor grove terrain <on|off>"),
                };
                break;
            default:
                return EditorCommandResult.Fail(
                    "Usage: editor grove <radius|spacing|density|terrain> <value>");
        }

        return EditorCommandResult.Ok(
            $"Grove brush: radius {brush.Radius:0.#} m, spacing {brush.Spacing:0.#} m, "
            + $"density {brush.Density:0.##}, terrain filter {(brush.RespectTerrain ? "on" : "off")}");

        static bool Number(string token, out float value)
            => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && float.IsFinite(value);
    }

    private static EditorCommandResult SetTerrain(string[] tokens, EditorToolSettings settings)
    {
        if (tokens.Length < 3)
            return EditorCommandResult.Fail("Usage: editor terrain <operation|shape|size|strength|material> ...");
        switch (tokens[1].ToLowerInvariant())
        {
            case "operation":
                settings.TerrainMode = tokens[2].ToLowerInvariant() switch
                {
                    "add" => EditMode.Add,
                    "subtract" => EditMode.Subtract,
                    _ => throw new ArgumentException("Operation must be add or subtract"),
                };
                break;
            case "shape":
                settings.TerrainShape = tokens[2].ToLowerInvariant() switch
                {
                    "sphere" => EditShape.Sphere,
                    "box" => EditShape.Box,
                    "organic" or "noisy" => EditShape.Organic,
                    _ => throw new ArgumentException("Shape must be sphere, box, or organic"),
                };
                break;
            case "size":
                if (tokens.Length is not (3 or 5)) throw new ArgumentException("Usage: editor terrain size <uniform|x y z>");
                float x = Positive(tokens[2]);
                settings.TerrainHalfExtent = tokens.Length == 3
                    ? new Vector3(x * 0.5f)
                    : new Vector3(x, Positive(tokens[3]), Positive(tokens[4])) * 0.5f;
                break;
            case "strength":
                float strength = ParseFloat(tokens[2]);
                if (strength is <= 0 or > 1) throw new ArgumentException("Strength must be in (0, 1]");
                settings.TerrainStrength = strength;
                break;
            case "material":
                if (!BlockCatalog.TryResolve(tokens[2], out var material))
                    throw new ArgumentException($"Unknown block: {tokens[2]}");
                settings.TerrainMaterial = material;
                break;
            default:
                throw new ArgumentException($"Unknown terrain setting: {tokens[1]}");
        }
        return EditorCommandResult.Ok(Status(settings, session: null));
    }

    private static EditorCommandResult SetBlock(string[] tokens, EditorToolSettings settings)
    {
        if (tokens.Length < 2)
            return EditorCommandResult.Fail("Usage: editor block <block-id|size> ...");

        switch (tokens[1].ToLowerInvariant())
        {
            case "size":
                if (tokens.Length is not (3 or 5))
                    return EditorCommandResult.Fail("Usage: editor block size <uniform|x y z>");
                int x = BlockDimension(tokens[2]);
                var size = tokens.Length == 3
                    ? new Int3(x, x, x)
                    : new Int3(x, BlockDimension(tokens[3]), BlockDimension(tokens[4]));
                BlockBrush.ValidateSize(size);
                settings.BlockSize = size;
                break;
            default:
                if (tokens.Length != 2 || !BlockCatalog.TryResolve(tokens[1], out var block))
                    return EditorCommandResult.Fail("Usage: editor block <block-id|size> ...");
                settings.Block = block;
                break;
        }

        settings.Mode = EditorToolMode.Block;
        return EditorCommandResult.Ok(Status(settings, session: null));
    }

    private static EditorCommandResult SetObject(string[] tokens, EditorToolSettings settings)
    {
        if (tokens.Length < 2)
            return EditorCommandResult.Fail(
                "Usage: editor object <pickup|crate|mob|spawn|conquest-flag|tree|grove|team|clear> ...");
        switch (tokens[1].ToLowerInvariant())
        {
            case "pickup":
                if (tokens.Length != 3 || !ItemCatalog.TryResolve(tokens[2], out var item))
                    return EditorCommandResult.Fail("Usage: editor object pickup <item-id>");
                settings.ObjectKind = EditorObjectChoiceKind.Pickup;
                settings.ObjectId = ItemCatalog.Id(item);
                break;
            case "crate":
                if (tokens.Length != 3 || !ItemCatalog.TryResolve(tokens[2], out var crated))
                    return EditorCommandResult.Fail("Usage: editor object crate <item-id>");
                settings.ObjectKind = EditorObjectChoiceKind.Crate;
                settings.ObjectId = ItemCatalog.Id(crated);
                break;
            case "mob":
                settings.ObjectKind = EditorObjectChoiceKind.Mob;
                settings.ObjectId = "demiurge:mob";
                break;
            case "spawn":
                settings.ObjectKind = EditorObjectChoiceKind.Spawn;
                settings.ObjectId = $"demiurge:spawn/{(tokens.Length > 2 ? tokens[2] : "default")}";
                break;
            case "conquest-flag":
                if (tokens.Length != 2)
                    return EditorCommandResult.Fail("Usage: editor object conquest-flag");
                settings.ObjectKind = EditorObjectChoiceKind.ConquestFlag;
                settings.ObjectId = "demiurge:conquest-flag";
                break;
            case "tree":
                if (tokens.Length != 2)
                    return EditorCommandResult.Fail("Usage: editor object tree");
                settings.ObjectKind = EditorObjectChoiceKind.Tree;
                settings.ObjectId = TreeBrush.ArchetypeId;
                break;
            case "grove":
                if (tokens.Length != 2)
                    return EditorCommandResult.Fail("Usage: editor object grove");
                settings.ObjectKind = EditorObjectChoiceKind.TreeBrush;
                settings.ObjectId = TreeBrush.ArchetypeId;
                break;
            case "team":
                if (tokens.Length != 3
                    || !int.TryParse(
                        tokens[2],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int team)
                    || team <= 0)
                    return EditorCommandResult.Fail("Usage: editor object team <positive-integer>");
                settings.ObjectTeam = team;
                break;
            case "clear":
                settings.ObjectKind = EditorObjectChoiceKind.None;
                settings.ObjectId = null;
                break;
            default:
                return EditorCommandResult.Fail($"Unknown object kind: {tokens[1]}");
        }
        settings.Mode = EditorToolMode.Object;
        return EditorCommandResult.Ok(
            $"Object: {settings.ObjectId ?? "none"} team={settings.ObjectTeam}");
    }

    private static EditorCommandResult Rotate(string[] tokens, EditorToolSettings settings)
    {
        if (tokens.Length != 2) return EditorCommandResult.Fail("Usage: editor rotate <degrees>");
        settings.ObjectYaw += ParseFloat(tokens[1]) * MathF.PI / 180f;
        return EditorCommandResult.Ok($"Object yaw: {settings.ObjectYaw * 180f / MathF.PI:0.##} degrees");
    }

    private static string Status(EditorToolSettings settings, EditorSession? session)
    {
        string status = $"mode={settings.Mode.ToString().ToLowerInvariant()} terrain={settings.TerrainMode.ToString().ToLowerInvariant()}/{settings.TerrainShape.ToString().ToLowerInvariant()} size={settings.TerrainHalfExtent * 2f} strength={settings.TerrainStrength:0.##} material={BlockCatalog.Id(settings.TerrainMaterial)} block={BlockCatalog.Id(settings.Block)} blockSize={settings.BlockSize.X}x{settings.BlockSize.Y}x{settings.BlockSize.Z} object={settings.ObjectId ?? "none"} team={settings.ObjectTeam}"
            + $" grove=r{settings.TreeBrush.Radius:0.#}/s{settings.TreeBrush.Spacing:0.#}"
            + $"/d{settings.TreeBrush.Density:0.##}"
            + $"/{(settings.TreeBrush.RespectTerrain ? "grass" : "any")}";
        if (session is null) return status;
        var diagnostics = session.Diagnostics();
        return status
            + $" dirty={session.Dirty} undo={session.History.UndoCount} redo={session.History.RedoCount}"
            + $" strokes={diagnostics.TerrainStrokeCount} blocks={diagnostics.BlockCount}"
            + $" maxChunkOps={diagnostics.MaximumChunkOverlap}";
    }

    private static float Positive(string token)
    {
        float value = ParseFloat(token);
        if (value <= 0) throw new ArgumentException("Size must be positive");
        return value;
    }

    private static int BlockDimension(string token)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value is < 1 or > EditorToolSettings.MaxBlockBrushDimension)
            throw new ArgumentException(
                $"Block dimensions must be integers from 1 to {EditorToolSettings.MaxBlockBrushDimension}");
        return value;
    }

    private static float ParseFloat(string token)
        => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
           && float.IsFinite(value)
            ? value
            : throw new FormatException($"Invalid number: {token}");
}
