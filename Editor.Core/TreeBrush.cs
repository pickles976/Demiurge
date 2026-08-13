using System.Numerics;

namespace Demiurge.Editor;

/// <summary>How the grove brush is set up. Spacing is stated as the distance the user cares about
/// and <see cref="TreeScatter.CellSizeFor"/> works backwards to a lattice that guarantees it.</summary>
public sealed record TreeBrushSettings
{
    public const float MinRadius = 1f;
    public const float MaxRadius = 60f;

    public float Radius { get; set; } = 12f;
    public float Spacing { get; set; } = 6f;
    public float Density { get; set; } = 0.7f;

    /// <summary>
    /// Whether the brush refuses ground that <see cref="TreePlacement.IsTreeEligible"/> dislikes —
    /// anything but walkable grass. On by default, because a grove painted across a cliff face looks
    /// like a mistake; off for when the author means it, since a tool that silently discards half a
    /// stroke is worse than one that does as it is told.
    /// </summary>
    public bool RespectTerrain { get; set; } = true;
}

/// <summary>
/// Turns a brush STROKE — the dabs left by a drag, the way the terrain brush records them — into
/// tree placements. The whole stroke resolves at once so it can be one undo step, and so spacing is
/// enforced across it rather than only within each dab.
///
/// The candidates come from a world-anchored lattice (see <see cref="TreeScatter"/>), which is what
/// makes the dabs safe to overlap: two dabs covering the same ground propose the SAME points, not
/// two sets of them, so a slow drag and a fast one paint the same grove.
///
/// The spacing test runs against every tree already in the document as well as the ones this stroke
/// has placed so far — so a hand-placed tree opens a gap in a painted grove, and two strokes at
/// different spacings still cannot overlap.
/// </summary>
public static class TreeBrush
{
    public const string ArchetypeId = "demiurge:tree";

    public static List<EditorPlacement> Paint(
        EditorDocument document,
        ChunkMap terrain,
        IReadOnlyList<Vector2> dabs,
        TreeBrushSettings settings)
    {
        if (dabs.Count == 0) return [];

        // Everything already standing, extended as the stroke places more, so candidates are tested
        // against each other as well as against the map.
        var taken = new List<Vector2>();
        foreach (var placement in document.Placements)
        {
            if (placement.Kind != EditorPlacementKind.Tree) continue;
            taken.Add(new Vector2(placement.Cell.X + 0.5f, placement.Cell.Z + 0.5f));
        }

        float spacingSq = settings.Spacing * settings.Spacing;
        var painted = new List<EditorPlacement>();
        var group = Guid.NewGuid();

        foreach (var dab in dabs)
        {
            foreach (var candidate in TreeScatter.InCircle(
                dab, settings.Radius, settings.Spacing, settings.Density))
            {
                int worldX = (int)MathF.Floor(candidate.Position.X);
                int worldZ = (int)MathF.Floor(candidate.Position.Y);

                if (settings.RespectTerrain
                    && !TreePlacement.IsTreeEligible(terrain, worldX, worldZ, out _)) continue;

                if (SurfaceQuery.HighestSurface(terrain, worldX, worldZ) is not { } surface) continue;

                var anchor = new Int3(worldX, (int)MathF.Floor(surface.Y), worldZ);
                var footprint = new Vector2(anchor.X + 0.5f, anchor.Z + 0.5f);

                // Also what de-duplicates a point proposed by two overlapping dabs: the second sees
                // the first sitting at zero distance.
                if (taken.Any(other => Vector2.DistanceSquared(other, footprint) < spacingSq)) continue;

                taken.Add(footprint);
                painted.Add(new EditorPlacement
                {
                    Id = Guid.NewGuid(),
                    Kind = EditorPlacementKind.Tree,
                    ArchetypeId = ArchetypeId,
                    Cell = anchor,
                    Yaw = candidate.Yaw,
                    Team = 0,
                    GroupId = group,
                });
            }
        }

        return painted;
    }

    /// <summary>Every tree the stroke passed over, once each however many dabs covered it.</summary>
    public static List<EditorPlacement> Erase(
        EditorDocument document, IReadOnlyList<Vector2> dabs, float radius)
    {
        if (dabs.Count == 0) return [];

        float radiusSq = radius * radius;
        return
        [
            .. document.Placements.Where(placement =>
                placement.Kind == EditorPlacementKind.Tree
                && dabs.Any(dab => Vector2.DistanceSquared(
                    new Vector2(placement.Cell.X + 0.5f, placement.Cell.Z + 0.5f), dab) <= radiusSq))
        ];
    }
}
