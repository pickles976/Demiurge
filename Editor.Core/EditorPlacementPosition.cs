using System.Numerics;

namespace Demiurge.Editor;

/// <summary>Resolves a saved placement cell to the local SDF surface used at runtime.</summary>
public static class EditorPlacementPosition
{
    /// <summary>
    /// How far up and down an actor placement looks for somewhere its body fits. Comfortably more
    /// than a storey, so a marker authored anywhere inside a building finds that building's floor,
    /// and short enough that a marker in mid-air over a valley does not bind to the valley floor.
    /// </summary>
    public const int ActorColumnRange = 8;

    public static Vector3 Resolve(ChunkMap terrain, EditorPlacement placement)
        => IsActor(placement.Kind)
            ? ResolveActorFeet(terrain, placement.Cell)
            : Resolve(terrain, placement.Cell);

    public static Vector3 Resolve(ChunkMap terrain, Int3 cell)
        => TryResolve(terrain, cell, out var position) ? position : cell.Centre;

    public static bool TryResolve(ChunkMap terrain, Int3 cell, out Vector3 position)
    {
        var center = cell.Centre;
        float? surfaceY = SurfaceQuery.NearestSurfaceY(
            terrain, center.X, center.Z, center.Y);
        position = surfaceY is { } y ? center with { Y = y } : center;
        return surfaceY.HasValue;
    }

    /// <summary>An actor placement: a player spawn or a mob, both of which arrive as a capsule.</summary>
    public static bool IsActor(EditorPlacementKind kind)
        => kind is EditorPlacementKind.PlayerSpawn or EditorPlacementKind.Mob;

    /// <summary>
    /// Where an actor authored at this cell actually stands.
    ///
    /// The plain resolve above finds the nearest SURFACE, which is not the same question: the top of
    /// the block a marker was dropped inside is a surface, and it is the roof. Asking instead for the
    /// nearest FREE SPACE — the closest point up or down the column where the capsule fits — puts a
    /// spawn authored inside a building on that building's floor, which is what was meant by putting
    /// it there.
    /// </summary>
    public static bool TryResolveActorFeet(ChunkMap terrain, Int3 cell, out Vector3 feet)
    {
        var centre = cell.Centre;
        bool found = NavTraversal.TryFindStandableNearestY(
            terrain, centre.X, centre.Z, centre.Y, ActorColumnRange, out float standingY);
        feet = found ? centre with { Y = standingY } : centre;
        return found;
    }

    public static Vector3 ResolveActorFeet(ChunkMap terrain, Int3 cell)
        // Nowhere in the column takes a body — solid rock for eight metres either way, or unloaded
        // terrain. Fall back to the surface resolve and push out of whatever it landed in, which is
        // the behaviour every actor placement had before free space was looked for at all.
        => TryResolveActorFeet(terrain, cell, out var feet)
            ? feet
            : ResolvePlayerFeet(terrain, Resolve(terrain, cell));

    public static Vector3 ResolvePlayerFeet(ChunkMap terrain, Vector3 surfacePosition)
    {
        var feet = surfacePosition;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            if (!TerrainCollision.TryDeepestContact(
                    terrain, PlayerMovement.Body, feet, out var contact))
                break;

            float penetration = PlayerMovement.Body.Radius - contact.Distance;
            if (penetration <= 0.001f) break;

            feet.Y += penetration / MathF.Max(contact.Normal.Y, 0.1f) + 0.001f;
        }
        return feet;
    }
}
