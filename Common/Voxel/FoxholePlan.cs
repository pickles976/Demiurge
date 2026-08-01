using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Where the next bite of a one-man fighting position goes.
    ///
    /// A foxhole is a SHAPE, not a depth, which is what the previous single-probe cut got wrong: it
    /// bit the same spot in front of the actor every time and produced a post-hole — deep enough to
    /// satisfy a depth check and too narrow to occupy. Cover you cannot stand in is not cover.
    ///
    /// So the pattern is explicit and the order is the behaviour: sink your own hole first, because
    /// until that is down you have no cover at all, then widen sideways and backwards. FORWARD is
    /// deliberately never cut — that lip is the parapet you shoot over, and removing it turns the
    /// position back into open ground facing the threat.
    ///
    /// Pure, so it is testable without a server: give it the field, where the actor is standing and
    /// which way the threat lies, and it answers with one voxel or nothing.
    /// </summary>
    public static class FoxholePlan
    {
        /// <summary>How far below the surrounding grade the floor is cut. Deep enough to crouch
        /// below, shallow enough to shoot over and to climb out of afterwards.</summary>
        public const float Depth = 1f;

        /// <summary>
        /// Offsets from the actor, in metres, as (right, forward) with forward pointing AT the
        /// threat. Ordered by priority, and the order is the tactic — the actor's own square comes
        /// first and the parapet square (positive forward) never appears.
        /// </summary>
        private static readonly (float Right, float Forward)[] Pattern =
        [
            (0f, 0f),        // under your own feet: the hole itself
            (-1f, 0f),       // widen left
            (1f, 0f),        // widen right
            (0f, -1f),       // and backwards, so there is room to move in it
        ];

        /// <summary>
        /// The next voxel to bite, or null when the position is finished.
        ///
        /// <paramref name="gradeY"/> is the surrounding ground level, measured outside the
        /// excavation by the caller — measuring it inside means the hole defines its own grade and
        /// the actor digs until it is buried.
        /// </summary>
        public static Vector3? NextBite(
            ChunkMap map,
            Vector3 feet,
            Vector3 toward,
            float gradeY)
        {
            if (toward.LengthSquared() < 1e-6f) return null;
            toward = Vector3.Normalize(new Vector3(toward.X, 0f, toward.Z));
            var right = new Vector3(toward.Z, 0f, -toward.X);
            float floorY = gradeY - Depth;

            foreach (var (offsetRight, offsetForward) in Pattern)
            {
                var spot = feet + right * offsetRight + toward * offsetForward;

                // Already at depth: this square is done, try the next one in the pattern. Checking
                // per SQUARE rather than once for the whole hole is what makes widening happen —
                // a single global depth check stops the moment the first square is deep enough.
                if (SurfaceQuery.HighestSurfaceY(map, (int)MathF.Floor(spot.X), (int)MathF.Floor(spot.Z))
                        is not { } surfaceY
                    || surfaceY <= floorY)
                    continue;

                if (TryBiteAt(map, spot, surfaceY) is { } target) return target;
            }

            return null;
        }

        private static Vector3? TryBiteAt(ChunkMap map, Vector3 spot, float surfaceY)
        {
            // Cast from just above this square's own surface rather than the actor's, so a square
            // the actor has already sunk below is still probed correctly.
            var probe = new Vector3(spot.X, surfaceY + 0.6f, spot.Z);
            if (TerrainRaycast.Cast(map, probe, -Vector3.UnitY, 1.5f) is not { } ground) return null;

            var target = Digging.TargetVoxel(ground.Point, ground.Normal);
            if (!map.TryGetVoxel(
                    (int)MathF.Round(target.X),
                    (int)MathF.Round(target.Y),
                    (int)MathF.Round(target.Z),
                    out var voxel)
                || voxel.Distance >= 0f
                || voxel.Material is not (BlockType.BlockType_Dirt or BlockType.BlockType_Grass))
                return null;

            return target;
        }
    }
}
