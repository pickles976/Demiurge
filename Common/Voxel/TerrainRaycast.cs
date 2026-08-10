using System.Numerics;

namespace Demiurge
{
    /// <summary>Where a ray met the terrain: the point on the surface, the outward normal there,
    /// and how far along the ray it was.</summary>
    public readonly record struct TerrainHit(Vector3 Point, Vector3 Normal, float Distance);

    /// <summary>
    /// Rays against the voxel field, by sphere tracing: sample the distance to the surface, step
    /// that far (nothing can be in the way, that is what a distance field promises), repeat.
    ///
    /// In Common because both ends need the same answer. The client aims with it and draws impacts
    /// where it says; the server has the authoritative map and would reach the same conclusion.
    ///
    /// World units ARE voxel units here — <see cref="TerrainCollision.TrySampleRaw"/> floors a world
    /// position straight into a voxel index — so a sampled distance is directly a step length, with
    /// no scaling in between.
    /// </summary>
    public static class TerrainRaycast
    {
        /// <summary>
        /// Close enough to the surface to stop marching and start bisecting. A shade under a
        /// hundredth of a voxel — finer than the field itself resolves (0.02 voxels), so this is
        /// never the thing limiting precision.
        /// </summary>
        const float SurfaceEpsilon = 0.008f;

        /// <summary>
        /// Bisections after the march crosses the surface. Each halves the bracket, so eight takes
        /// a step of at most 2.54 voxels down to ~0.01 — sub-voxel, which is what an impact effect
        /// and a reticle both need to not visibly float.
        /// </summary>
        const int RefineIterations = 8;

        /// <summary>
        /// Stored distance saturates here (<see cref="Voxel.Maximum"/> / <see cref="Voxel.Scale"/>),
        /// so no single step can ever be longer than this however far away the terrain really is.
        /// That is what makes the step budget below a bound rather than a guess.
        /// </summary>
        const float MaxStep = Voxel.Maximum / Voxel.Scale;

        /// <summary>
        /// Extra steps allowed for a ray converging on a surface it approaches at a shallow angle.
        /// </summary>
        /// <remarks>
        /// Sphere tracing steps by the distance to the surface, so a ray running nearly parallel to a
        /// slope closes that distance by a small FRACTION each time. The approach is geometric, not
        /// linear: one measured ray shrank its clearance about 3% per step, from 1.55 down to 0.03 over
        /// a hundred steps, and needed roughly 153 to reach <see cref="SurfaceEpsilon"/>.
        ///
        /// The budget used to be 64 on top of the open-air term — about 119 all told — which stopped
        /// that ray at 37.8 m with solid ground beginning at 38.75 m. Sized from the convergence
        /// instead: reaching <see cref="SurfaceEpsilon"/> from <see cref="MaxStep"/> at a ratio r takes
        /// ln(eps/MaxStep) / ln(r) steps, so 1024 covers everything down to about r = 0.994. Past that
        /// the ray is parallel to the surface for practical purposes, and the exhaustion path below
        /// handles it.
        ///
        /// Only grazing rays ever pay this; a ray through open air still finishes in about 24 steps.
        /// </remarks>
        const int GrazingStepAllowance = 1024;

        /// <summary>
        /// Fires a ray and returns the first surface it meets, or null for a clean miss.
        ///
        /// Null ALSO means the ray ran out of loaded terrain — deliberately a miss rather than a hit,
        /// which is the opposite of what <see cref="TerrainCollision"/> does with the same condition.
        /// The reasons differ: collision must never let a player fall through a chunk that has not
        /// arrived, so it treats unloaded as solid; a ray that invents a surface in unloaded space
        /// would put a bullet hole in mid-air, so it treats unloaded as the end of the world. Aiming
        /// at the sky lands here too, since a ray leaving the world vertically stops finding voxels.
        ///
        /// A ray starting INSIDE terrain hits immediately at its origin — an honest answer for a
        /// muzzle buried in a wall, rather than shooting out through the back of it.
        /// </summary>
        public static TerrainHit? Cast(ChunkMap map, Vector3 origin, Vector3 direction, float maxDistance)
        {
            float length = direction.Length();
            if (length < 1e-6f || maxDistance <= 0f) return null;

            direction /= length;

            // Ordinary open-air cost, plus the grazing allowance below. The first term is what a ray
            // through clear space needs; on its own it is nowhere near enough for a shallow approach.
            int maxSteps = (int)(maxDistance / MaxStep) + 4 * RefineIterations + GrazingStepAllowance;

            // ONE memo for the whole march. Each step samples the field 64 times and the old code
            // built a fresh cursor for each of the two calls, so a 40 m ray threw the chunk lookup
            // away thousands of times over — while consecutive steps are at most MaxStep apart and
            // therefore nearly always in the chunk the previous step already resolved. Same
            // arithmetic, same hit, far fewer dictionary probes.
            var cursor = new VoxelCursor(map);

            float travelled = 0f;
            float previous = 0f;
            bool havePrevious = false;

            for (int step = 0; step < maxSteps && travelled <= maxDistance; step++)
            {
                var at = origin + direction * travelled;

                // Eight voxel reads, not 56. The march needs a safe step length and a "have I arrived"
                // test; it does NOT need a surface normal, and the smoothed central-difference gradient
                // that TrySample divides by is 48 of those 56 reads. The normal is resampled once, at
                // the hit, in At(). See TrySampleCellCorrected for why the per-cell gradient is a sound
                // substitute for THIS decision and not for shading or pushout.
                if (!TerrainCollision.TrySampleCellCorrected(ref cursor, at, out float raw, out float corrected))
                    return null;   // ran out of loaded world

                if (corrected <= SurfaceEpsilon)
                {
                    // Bisect between the last known-outside sample and this known-inside one. On the
                    // very first sample there is no outside to bracket with — the ray started in
                    // terrain — so report the origin rather than inventing a crossing behind it.
                    float hit = havePrevious
                        ? Refine(ref cursor, origin, direction, previous, travelled)
                        : travelled;
                    return At(ref cursor, origin, direction, hit);
                }

                previous = travelled;
                havePrevious = true;

                // The clamp is what guarantees progress. A grazing ray samples a distance that
                // approaches zero without ever crossing, and stepping by it would converge in place.
                travelled += MathF.Max(SafeStep(raw, corrected), SurfaceEpsilon);
            }

            // Two ways out of that loop, and they mean opposite things.
            //
            // Past maxDistance is a genuine miss: the ray was still taking real steps and simply ran
            // out of range. Out of STEPS is not — every step advances at least SurfaceEpsilon, so
            // exhausting the budget means the average step was a fraction of a voxel, which only
            // happens when the ray is converging on a surface it never formally crossed.
            //
            // Reporting that as a miss is what let line of sight pass through a hillside: the caller
            // cannot distinguish "nothing there" from "I gave up next to something". Report the
            // converged position instead. It is within a hundredth of a voxel of the surface, and for
            // both of the things that ask — can this NPC see that one, where does this bullet land —
            // treating a graze as contact is the answer that does not invent open sky.
            return travelled > maxDistance ? null : At(ref cursor, origin, direction, travelled);
        }

        /// <summary>
        /// How far it is safe to advance. Sphere tracing is only correct while the step is a LOWER
        /// bound on the true distance to the surface; overstep once and the ray is through the
        /// hillside with nothing to notice it happened.
        ///
        /// Neither value on its own is that bound, and they fail in opposite places:
        ///
        ///  - RAW is the vertical gap to the surface, so on a slope it overstates the real distance
        ///    by 1/cos(slope) — a factor of two at 60 degrees. Stepping by it tunnels through ridges.
        ///  - CORRECTED divides that by the gradient length, which is exactly right where the
        ///    gradient means something. But the stored field saturates at +/-2.54 voxels, and across
        ///    a saturation boundary the gradient collapses toward zero, so dividing by it INFLATES
        ///    the distance instead — measured at 9.4 where the truth was 3.0, enough to jump a
        ///    four-voxel slab in one step.
        ///
        /// The smaller of the two is safe in both regimes, and for a reason rather than by luck:
        /// where the field saturates, raw is clamped and therefore UNDERSTATES the real gap, which
        /// is the harmless direction; where it does not saturate, corrected is the true distance.
        /// </summary>
        static float SafeStep(float raw, float corrected) => MathF.Min(MathF.Min(raw, corrected), MaxStep);

        /// <summary>
        /// Narrows a bracket that straddles the surface — <paramref name="outside"/> in air,
        /// <paramref name="inside"/> at or past it — down to the crossing.
        ///
        /// A sample that falls in unloaded terrain keeps the bracket rather than aborting: the two
        /// ends were both good, so the crossing is real and still between them, and giving up here
        /// would turn a genuine hit into a miss at a chunk seam.
        /// </summary>
        /// <remarks>
        /// Samples raw rather than corrected, which is not an approximation — it is the same test.
        /// The corrected distance is <c>raw / |gradient|</c> and the gradient length is positive, so
        /// <c>corrected &lt;= 0</c> and <c>raw &lt;= 0</c> are the same predicate. Eight reads per
        /// iteration instead of 56, bit-identical bracket.
        /// </remarks>
        static float Refine(ref VoxelCursor cursor, Vector3 origin, Vector3 direction, float outside, float inside)
        {
            for (int i = 0; i < RefineIterations; i++)
            {
                float middle = 0.5f * (outside + inside);
                if (!TerrainCollision.TrySampleRaw(ref cursor, origin + direction * middle, out float raw)) break;

                if (raw <= 0f) inside = middle;
                else outside = middle;
            }

            return inside;
        }

        /// <summary>
        /// The hit at a known distance along the ray. The normal is resampled here rather than
        /// carried out of the march because the march's last sample sits wherever the stepping left
        /// it, which is not the surface.
        /// </summary>
        static TerrainHit At(ref VoxelCursor cursor, Vector3 origin, Vector3 direction, float travelled)
        {
            var point = origin + direction * travelled;
            var normal = TerrainCollision.TrySample(ref cursor, point, out var field) ? field.Normal : -direction;
            return new TerrainHit(point, normal, travelled);
        }
    }
}
