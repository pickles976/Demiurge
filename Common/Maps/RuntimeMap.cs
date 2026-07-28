using System.Numerics;

namespace Demiurge;

public enum RuntimePlacementKind : byte
{
    Pickup = 1,
    Mob = 2,
    PlayerSpawn = 3,
}

public readonly record struct RuntimePlacement(
    RuntimePlacementKind Kind,
    Vector3 Position,
    float Yaw = 0f,
    ItemType Item = default,
    string SpawnId = "default");

public sealed class RuntimeMap
{
    public const int CurrentFormatVersion = 1;

    public required Guid MapId { get; init; }
    public required string Name { get; init; }
    public required ChunkMap Terrain { get; init; }
    public required IReadOnlyList<RuntimePlacement> Placements { get; init; }
    public byte[] SourceHash { get; init; } = new byte[RuntimeMapSerializer.HashBytes];
    public byte[] ContentHash { get; internal set; } = new byte[RuntimeMapSerializer.HashBytes];
}

public readonly record struct RuntimeMapValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}
