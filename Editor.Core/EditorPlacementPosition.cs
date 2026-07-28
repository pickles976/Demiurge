using System.Numerics;

namespace Demiurge.Editor;

/// <summary>Resolves a saved placement cell to the local SDF surface used at runtime.</summary>
public static class EditorPlacementPosition
{
    public static Vector3 Resolve(ChunkMap terrain, EditorPlacement placement)
    {
        var position = Resolve(terrain, placement.Cell);
        return placement.Kind == EditorPlacementKind.PlayerSpawn
            ? ResolvePlayerFeet(terrain, position)
            : position;
    }

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
