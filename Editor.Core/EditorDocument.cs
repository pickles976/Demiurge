using System.Numerics;

namespace Demiurge.Editor;

public readonly record struct Int3(int X, int Y, int Z)
{
    public Vector3 SamplePosition => new(X, Y, Z);
    public Vector3 Centre => new(X + 0.5f, Y + 0.5f, Z + 0.5f);
}

public readonly record struct Float3(float X, float Y, float Z)
{
    public Vector3 Vector => new(X, Y, Z);
    public static Float3 From(Vector3 value) => new(value.X, value.Y, value.Z);
}

public sealed record BaseTerrainDefinition
{
    /// <summary>The noise terrain every ordinary map is carved out of.</summary>
    public const string NoiseGenerator = "demiurge:terrain-v1";

    /// <summary>
    /// The structure editor's level pad. Not a map anybody plays: it exists so a structure can be
    /// built against a flat reference and measured against a gridded floor.
    /// </summary>
    public const string FlatDebugGenerator = "demiurge:flat-debug";

    public string GeneratorId { get; init; } = NoiseGenerator;
    public int Seed { get; init; } = 100;
    public int MinChunkX { get; init; } = WorldGen.Min.x;
    public int MinChunkZ { get; init; } = WorldGen.Min.z;
    public int MaxChunkX { get; init; } = WorldGen.Max.x;
    public int MaxChunkZ { get; init; } = WorldGen.Max.z;

    /// <summary>Height of the flat pad's surface. Ignored by the noise generator.</summary>
    public int FloorY { get; init; } = StructureWorld.FloorY;

    /// <summary>Half the flat pad's side in metres. Ignored by the noise generator.</summary>
    public float PadHalfExtent { get; init; } = StructureWorld.HalfExtent;
}

/// <summary>
/// The shape of the structure editor's world, in one place because three things have to agree
/// about it: the generator that builds the floor, the chunk range the document evaluates, and the
/// camera that has to start standing on it.
/// </summary>
public static class StructureWorld
{
    /// <summary>A 100 m square, centred on the world origin.</summary>
    public const float SideMetres = 100f;
    public const float HalfExtent = SideMetres / 2f;

    /// <summary>
    /// Well clear of the bedrock plane, so there is room to cut down into the pad as well as build
    /// up from it, and low enough to leave most of the 128 m column overhead.
    /// </summary>
    public const int FloorY = 32;

    /// <summary>
    /// Chunks the document evaluates. One chunk wider on every side than the pad, because the
    /// outermost ring of a map is a meshing neighbour and never drawn — without the margin the
    /// last few metres of the pad would be there in the field and missing from the picture.
    /// </summary>
    public static (int Min, int Max) ChunkBounds
    {
        get
        {
            int edge = (int)MathF.Ceiling(HalfExtent / ChunkConstants.ChunkWidth);
            return (-edge - 1, edge);
        }
    }
}

public sealed record TerrainStroke
{
    public required Guid Id { get; init; }
    public required long Sequence { get; init; }
    public required EditMode Mode { get; init; }
    public required EditShape Shape { get; init; }
    public required Float3 HalfExtent { get; init; }
    public required float Strength { get; init; }
    public required string MaterialId { get; init; }
    public required List<Float3> Dabs { get; init; }
}

public sealed record EditorBlockPlacement
{
    public required Guid Id { get; init; }
    public required long Sequence { get; init; }
    public required Int3 Cell { get; init; }
    public required string BlockId { get; init; }
    public Guid? GroupId { get; init; }
}

public enum EditorPlacementKind
{
    Pickup,
    Mob,
    PlayerSpawn,
    ConquestFlag,

    /// <summary>A weapon pickup crated as map supply. The archetype is the item it hands out.</summary>
    SupplyCrate,

    /// <summary>Scenery, placed singly here and by the grove brush later.</summary>
    Tree,
}

public sealed record EditorPlacement
{
    public required Guid Id { get; init; }
    public required EditorPlacementKind Kind { get; init; }
    public required string ArchetypeId { get; init; }
    public required Int3 Cell { get; init; }
    public float Yaw { get; init; }
    public string? WeaponId { get; init; }
    /// <summary>Zero is neutral; playable teams use positive integers.</summary>
    public int Team { get; init; } = 1;
    public Guid? GroupId { get; init; }
}

public sealed record EditorDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid MapId { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public BaseTerrainDefinition BaseTerrain { get; init; } = new();
    public List<TerrainStroke> TerrainStrokes { get; init; } = [];
    public List<EditorBlockPlacement> Blocks { get; init; } = [];
    public List<EditorPlacement> Placements { get; init; } = [];

    /// <summary>
    /// Whether this is the structure editor's scratch pad rather than a map.
    ///
    /// Asked by everything that would WRITE the document to disk. A structure world has no source
    /// file and must not acquire one — including through the autosave, which would otherwise create
    /// a map directory for a world nobody asked to keep.
    /// </summary>
    public bool IsStructureWorld
        => BaseTerrain.GeneratorId == BaseTerrainDefinition.FlatDebugGenerator;

    public static EditorDocument Create(string name)
    {
        var baseMap = new ChunkMap();
        baseMap.Insert(ChunkGenerator.GenerateChunk(new ChunkIndex { x = 0, z = 0 }));
        float spawnY = SurfaceQuery.SurfacePosition(baseMap, 0.5f, 0.5f).Y;

        return new()
        {
            Name = MapPathResolver.ValidateName(name),
            Placements =
            [
                new EditorPlacement
                {
                    Id = Guid.NewGuid(),
                    Kind = EditorPlacementKind.PlayerSpawn,
                    ArchetypeId = "demiurge:spawn/default",
                    Cell = new Int3(0, (int)MathF.Floor(spawnY), 0),
                    Team = 1,
                },
            ],
        };
    }

    /// <summary>
    /// A scratch world for authoring structures: a flat, gridded 100 m pad and nothing else.
    ///
    /// It carries no player spawn, because it is never played and never baked — the only thing
    /// that leaves it is a saved structure. That is also why it is not created through
    /// <see cref="MapRepository"/>: there is no file, and a structure world persisted under
    /// maps/ would be a map you must remember never to edit.
    /// </summary>
    public static EditorDocument CreateStructureWorld(string name)
    {
        (int min, int max) = StructureWorld.ChunkBounds;
        return new()
        {
            Name = MapPathResolver.ValidateName(name),
            BaseTerrain = new BaseTerrainDefinition
            {
                GeneratorId = BaseTerrainDefinition.FlatDebugGenerator,
                MinChunkX = min,
                MinChunkZ = min,
                MaxChunkX = max,
                MaxChunkZ = max,
            },
        };
    }
}
