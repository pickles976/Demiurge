using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Where each man in a squad walks, relative to the squad's objective.
    ///
    /// A V — point man forward, the rest echeloned back and alternating sides. It replaces a
    /// golden-angle ring around the destination, which spread men out only once they had ARRIVED:
    /// on the way they all steered at the same point and travelled as a clump, which is one grenade
    /// or one burst for the whole squad.
    ///
    /// The V is oriented along the approach, so the offsets mean the same thing throughout the move
    /// — the flanks stay on the flanks instead of rotating into file as the bearing changes. Slots
    /// are assigned by roster position and roster order is stable, so a man keeps his place in the
    /// formation rather than swapping with a neighbour every time the squad re-plans.
    /// </summary>
    public static class WedgeFormation
    {
        /// <summary>
        /// Lateral gap between neighbouring files. Wide enough that one burst or one grenade cannot
        /// take two men — GrenadeConfig.DamageRadius is 10 m, so anything under that is a shared
        /// casualty waiting to happen — while still close enough to support each other.
        /// </summary>
        public const float Spacing = 14f;

        /// <summary>How far back each rank sits. Shallower than the spacing so the shape reads as a
        /// V rather than a column: the flanks lead their own axis while staying off the point.</summary>
        public const float Depth = 9f;

        /// <summary>
        /// The position for the <paramref name="slot"/>th man of a squad heading for
        /// <paramref name="objective"/> from <paramref name="approachFrom"/>.
        ///
        /// Slot 0 is the point. Odd slots go right, even slots left, each pair one rank further
        /// back, so a squad of any size stays symmetric about its axis of advance.
        /// </summary>
        public static Vector3 Slot(Vector3 objective, Vector3 approachFrom, int slot)
        {
            if (slot <= 0) return objective;

            var forward = objective - approachFrom;
            forward.Y = 0f;

            // Degenerate approach (the squad is standing on its objective): any axis will do, and a
            // fixed one keeps the answer deterministic.
            forward = forward.LengthSquared() < 1e-4f
                ? Vector3.UnitZ
                : Vector3.Normalize(forward);
            var right = new Vector3(forward.Z, 0f, -forward.X);

            int rank = (slot + 1) / 2;                  // 1,1,2,2,3,3...
            float side = slot % 2 == 1 ? 1f : -1f;      // right, left, right, left...

            return objective
                 + right * (side * rank * Spacing)
                 - forward * (rank * Depth);
        }
    }
}
