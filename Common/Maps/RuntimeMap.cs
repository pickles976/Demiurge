using System.Numerics;

namespace Demiurge;

public enum RuntimePlacementKind : byte
{
    Pickup = 1,
    Mob = 2,
    PlayerSpawn = 3,
    Flag = 4,

    /// <summary>
    /// A weapon pickup presented as a supply crate rather than as the weapon itself. Same item and
    /// same pickup rules as <see cref="Pickup"/> — only the world presentation differs, which is why
    /// it is a placement kind and not an item.
    /// </summary>
    SupplyCrate = 5,
}

public readonly record struct RuntimePlacement(
    RuntimePlacementKind Kind,
    Vector3 Position,
    float Yaw = 0f,
    ItemType Item = default,
    string SpawnId = "default",
    int Team = 1);

public sealed class RuntimeMap
{
    /// <summary>
    /// What a bake of this map means, not only how its bytes are laid out.
    ///
    /// Bumped when the BAKER changes as well as when the format does, because the editor decides a
    /// cached bake is current by comparing the source hash — and a change to how a placement
    /// resolves leaves the source identical while making every existing bake wrong. That is not
    /// hypothetical: version 4 exists because actor placements began resolving to free space
    /// instead of the nearest surface, and until this number moved, every session went on loading a
    /// bake that still had the spawn markers sitting on a roof.
    /// </summary>
    public const int CurrentFormatVersion = 4;

    /// <summary>The version this map was read at; <see cref="CurrentFormatVersion"/> for a fresh
    /// bake. Older means the file predates the current baker, not that it failed to load.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

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
