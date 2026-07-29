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
    public string GeneratorId { get; init; } = "demiurge:terrain-v1";
    public int Seed { get; init; } = 100;
    public int MinChunkX { get; init; } = WorldGen.Min.x;
    public int MinChunkZ { get; init; } = WorldGen.Min.z;
    public int MaxChunkX { get; init; } = WorldGen.Max.x;
    public int MaxChunkZ { get; init; } = WorldGen.Max.z;
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
    Flag,
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
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid MapId { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public BaseTerrainDefinition BaseTerrain { get; init; } = new();
    public List<TerrainStroke> TerrainStrokes { get; init; } = [];
    public List<EditorBlockPlacement> Blocks { get; init; } = [];
    public List<EditorPlacement> Placements { get; init; } = [];

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
}
