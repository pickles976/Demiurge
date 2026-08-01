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

            // Every step advances at least SurfaceEpsilon (below), so this cannot spin: it is the
            // budget for the pathological case of grazing a surface, where steps stay tiny. The
            // MaxStep term is the ordinary open-air cost.
            int maxSteps = (int)(maxDistance / MaxStep) + 4 * RefineIterations + 64;

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

                // Both values from one sample. The raw distance is a by-product of the corrected
                // one — same eight corners, same trilinear evaluation — and asking for it separately
                // repeated the whole thing at the identical point, once per step, for the length of
                // every ray.
                if (!TerrainCollision.TrySample(ref cursor, at, out var point, out float raw))
                    return null;   // ran out of loaded world

                if (point.Distance <= SurfaceEpsilon)
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
                travelled += MathF.Max(SafeStep(raw, point.Distance), SurfaceEpsilon);
            }

            return null;
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
        static float Refine(ref VoxelCursor cursor, Vector3 origin, Vector3 direction, float outside, float inside)
        {
            for (int i = 0; i < RefineIterations; i++)
            {
                float middle = 0.5f * (outside + inside);
                if (!TerrainCollision.TrySample(ref cursor, origin + direction * middle, out var point)) break;

                if (point.Distance <= 0f) inside = middle;
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
