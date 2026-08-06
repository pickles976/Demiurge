using System.Numerics;

namespace Demiurge.Editor;

/// <summary>
/// The world box a placement occupies. Picking and the viewport highlight both go through here, so
/// the box you see outlined is exactly the box the ray tested — a highlight derived separately would
/// drift from what a click actually hits.
/// </summary>
public static class EditorPlacementBounds
{
    /// <summary>Bounds relative to the resolved placement position, which sits on the surface.</summary>
    public static (Vector3 Min, Vector3 Max) Local(EditorPlacementKind kind) => kind switch
    {
        // The flag pole is thin; a pole-width box would be near impossible to click.
        EditorPlacementKind.Flag =>
            (new Vector3(-0.35f, 0f, -0.35f), new Vector3(0.35f, 2.4f, 0.35f)),
        // The crate model is about 1 m long, 0.6 wide and 0.42 tall, resting on the placement.
        EditorPlacementKind.SupplyCrate =>
            (new Vector3(-0.35f, 0f, -0.55f), new Vector3(0.35f, 0.5f, 0.55f)),
        EditorPlacementKind.Mob or EditorPlacementKind.PlayerSpawn =>
            (new Vector3(-PlayerMovement.Body.Radius, 0f, -PlayerMovement.Body.Radius),
             new Vector3(PlayerMovement.Body.Radius, PlayerMovement.Body.Height, PlayerMovement.Body.Radius)),
        _ => (new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 1f, 0.5f)),
    };

    public static (Vector3 Min, Vector3 Max) World(ChunkMap terrain, EditorPlacement placement)
    {
        var position = EditorPlacementPosition.Resolve(terrain, placement);
        var (min, max) = Local(placement.Kind);
        return (position + min, position + max);
    }
}

/// <summary>Ray picking over placement bounds, used for selection, moving, and deletion.</summary>
public static class EditorPlacementPicker
{
    /// <summary>
    /// The nearest placement the ray enters within <paramref name="maxDistance"/>, which callers set
    /// to the terrain hit distance so an object behind a hill cannot be picked through it.
    /// </summary>
    public static Guid? Pick(
        IEnumerable<EditorPlacement> placements,
        ChunkMap terrain,
        Vector3 origin,
        Vector3 direction,
        float maxDistance)
    {
        Guid? best = null;
        float bestDistance = maxDistance;
        foreach (var placement in placements)
        {
            var (min, max) = EditorPlacementBounds.World(terrain, placement);
            if (RayBox(origin, direction, min, max, out float distance) && distance < bestDistance)
            {
                best = placement.Id;
                bestDistance = distance;
            }
        }
        return best;
    }

    public static bool RayBox(
        Vector3 origin,
        Vector3 direction,
        Vector3 min,
        Vector3 max,
        out float distance)
    {
        float near = 0f;
        float far = float.MaxValue;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            float hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi) { distance = 0f; return false; }
                continue;
            }
            float t1 = (lo - o) / d;
            float t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            near = MathF.Max(near, t1);
            far = MathF.Min(far, t2);
            if (near > far) { distance = 0f; return false; }
        }
        distance = near;
        return true;
    }
}
