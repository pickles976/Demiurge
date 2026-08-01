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
        /// <summary>Depth of the protected centre. The position starts as a one-voxel, two-metre
        /// deep scrape before any widening begins.</summary>
        public const float Depth = 2f;

        /// <summary>The rear shelf is deep enough to crouch behind the parapet but shallow enough
        /// to step onto while leaving the centre.</summary>
        public const float RearShelfDepth = 1f;

        /// <summary>The side shelves are standing firing steps: an actor can see and shoot over
        /// the intact threat-side lip without having to jump out of the position.</summary>
        public const float FiringShelfDepth = 1f;

        /// <summary>
        /// Offsets from the actor, in metres, as (right, forward) with forward pointing AT the
        /// threat. Ordered by priority, and the order is the tactic — the actor's own square comes
        /// first and the parapet square (positive forward) never appears.
        /// </summary>
        private static readonly (float Right, float Forward, float Depth)[] Pattern =
        [
            // This entry is deliberately first and deepest. NextBite does not advance to another
            // entry until its requested depth is complete, so the initial position really is a
            // 1x1, two-deep hole rather than four shallow bowls dug in rotation.
            (0f, 0f, Depth),
            (0f, -1f, RearShelfDepth),
            (-1f, 0f, FiringShelfDepth),
            (1f, 0f, FiringShelfDepth),
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
            foreach (var (offsetRight, offsetForward, depth) in Pattern)
            {
                var spot = feet + right * offsetRight + toward * offsetForward;
                if (TryBiteAt(map, spot, gradeY, depth) is { } target) return target;
            }

            return null;
        }

        private static Vector3? TryBiteAt(
            ChunkMap map,
            Vector3 spot,
            float gradeY,
            float depth)
        {
            int x = (int)MathF.Floor(spot.X);
            int z = (int)MathF.Floor(spot.Z);
            int top = (int)MathF.Floor(gradeY);
            int layers = Math.Max(1, (int)MathF.Ceiling(depth));
            for (int layer = 0; layer < layers; layer++)
            {
                int y = top - layer;
                if (!map.TryGetVoxel(x, y, z, out var voxel)) return null;

                // A half-strength bite can relabel the centre sample Air before the spherical cut
                // is actually clear. Keep hitting it until the same clearance threshold used by
                // staircase excavation is reached; otherwise every layer receives only half a bite
                // and the nominal two-deep centre remains a shallow dent.
                if (voxel.Distance >= 0.5f) continue;
                if (voxel.Distance < 0f
                    && voxel.Material is not (
                        BlockType.BlockType_Dirt or BlockType.BlockType_Grass))
                    return null;
                return new Vector3(x, y, z);
            }

            return null;
        }
    }
}
