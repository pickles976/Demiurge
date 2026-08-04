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
        /// Closest two men may be placed. GrenadeConfig.DamageRadius is 10 m, so anything under this
        /// is a shared casualty waiting to happen — it is a floor, not a preference, and it is why
        /// <see cref="SpacingFor"/> derives downward from the envelope but never past this.
        /// </summary>
        public static float MinimumSpacing => GrenadeConfig.DamageRadius;

        /// <summary>
        /// How far the outermost man may sit from the objective, laterally.
        ///
        /// This bound is the whole reason spacing is derived rather than fixed. At a fixed 14 m the
        /// envelope grew with the squad: four men put the flanks 28 m out, six men put them 42 m out
        /// and 27 m back — about 50 m from the objective, off its tactical ground and frequently onto
        /// terrain nobody can stand on. The man would path there, never arrive, and trip the
        /// sixty-second stuck watchdog. Squad size and formation width were coupled and nothing
        /// reconciled them.
        /// </summary>
        public const float MaximumEnvelopeWidth = 30f;

        /// <summary>
        /// Lateral gap between neighbouring files for a squad of this size: as wide as the envelope
        /// allows, never tighter than one grenade.
        /// </summary>
        public static float SpacingFor(int squadSize)
            => MathF.Max(MinimumSpacing, MaximumEnvelopeWidth / MathF.Max(1, RankOf(squadSize - 1)));

        /// <summary>How far back each rank sits, as a fraction of the lateral gap. Shallower than the
        /// spacing so the shape reads as a V rather than a column: the flanks lead their own axis
        /// while staying off the point.</summary>
        public const float DepthRatio = 9f / 14f;

        private static int RankOf(int slot) => (slot + 1) / 2;   // 0,1,1,2,2,3,3...

        /// <summary>
        /// The position for the <paramref name="slot"/>th man of a squad heading for
        /// <paramref name="objective"/> from <paramref name="approachFrom"/>.
        ///
        /// Slot 0 is the point. Odd slots go right, even slots left, each pair one rank further
        /// back, so a squad of any size stays symmetric about its axis of advance.
        ///
        /// <paramref name="squadSize"/> is what keeps the envelope bounded — see
        /// <see cref="MaximumEnvelopeWidth"/>. A bigger squad packs tighter rather than reaching
        /// further, down to the one-grenade floor.
        /// </summary>
        public static Vector3 Slot(Vector3 objective, Vector3 approachFrom, int slot, int squadSize)
        {
            if (slot <= 0) return objective;

            float spacing = SpacingFor(squadSize);
            float depth = spacing * DepthRatio;

            var forward = objective - approachFrom;
            forward.Y = 0f;

            // Degenerate approach (the squad is standing on its objective): any axis will do, and a
            // fixed one keeps the answer deterministic.
            forward = forward.LengthSquared() < 1e-4f
                ? Vector3.UnitZ
                : Vector3.Normalize(forward);
            var right = new Vector3(forward.Z, 0f, -forward.X);

            int rank = RankOf(slot);
            float side = slot % 2 == 1 ? 1f : -1f;      // right, left, right, left...

            return objective
                 + right * (side * rank * spacing)
                 - forward * (rank * depth);
        }
    }
}
