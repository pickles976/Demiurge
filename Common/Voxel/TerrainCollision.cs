using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// A body's contact with the field at one point: how far away the surface is, and which way is
    /// out of it. Distance is CORRECTED (see <see cref="TerrainCollision.TrySample"/>) — negative
    /// means the point is inside terrain. <see cref="Normal"/> is smoothed across cells for stable
    /// pushout; <see cref="SurfaceNormal"/> is the exact containing-cell derivative used to classify
    /// steep slopes without neighboring density saturation biasing the result.
    /// </summary>
    public readonly record struct FieldPoint(float Distance, Vector3 Normal, Vector3 SurfaceNormal);

    /// <summary>
    /// The player's body, as collision sees it: a vertical capsule, tested as a small stack of
    /// spheres up its axis. Positions are the FEET, matching <see cref="SurfaceQuery.SurfacePosition"/>
    /// and what the view renders from.
    /// </summary>
    public readonly record struct CapsuleBody(float Radius, float Height)
    {
        /// <summary>
        /// Spheres sampled along the axis. Three is enough for a body twice as tall as it is wide:
        /// consecutive spheres overlap, so there is no gap for a thin ledge to slip through.
        /// </summary>
        public const int SampleCount = 3;

        /// <summary>
        /// Centre of sample sphere <paramref name="i"/> for a body whose feet are at
        /// <paramref name="feet"/>. The segment is inset by the radius at both ends so the capsule's
        /// caps stay inside the body's declared height rather than bulging past it.
        /// </summary>
        public Vector3 SampleCenter(Vector3 feet, int i)
        {
            float low = Radius;
            float high = MathF.Max(Height - Radius, Radius);
            float t = SampleCount == 1 ? 0f : i / (float)(SampleCount - 1);

            return feet with { Y = feet.Y + low + (high - low) * t };
        }
    }

    /// <summary>
    /// Collision against the voxel field. Engine-free and in Common because the server is
    /// authoritative over movement and the client has to predict it with exactly the same maths.
    ///
    /// Reads the field on the SAME grid the mesher does: voxel (x, y, z) is the sample at world
    /// position exactly (x, y, z), never a cell centre. See the edge crossings in
    /// <see cref="ChunkMesher"/> — they are built at integer positions. Offsetting this by half a
    /// voxel would put collision half a voxel away from the surface you can see.
    /// </summary>
    public static class TerrainCollision
    {
        /// <summary>Half-width of the smoothed collision-normal stencil. The exact cell derivative
        /// is retained separately for slope classification.</summary>
        const float GradientStep = 0.5f;

        /// <summary>
        /// Below this the gradient carries no usable direction. Happens for real: stored distance
        /// saturates around +/-2.54 voxels (<see cref="Voxel.Scale"/>), so deep inside terrain every
        /// sample in the stencil reads the same clamped value and the difference is exactly zero.
        /// </summary>
        const float MinGradientLength = 1e-4f;

        /// <summary>
        /// Trilinear sample of the raw stored field. False means at least one of the eight corner
        /// voxels has no data — an unloaded chunk — which callers treat as impassable, never as air.
        /// </summary>
        public static bool TrySampleRaw(ChunkMap map, Vector3 p, out float distance)
        {
            var cursor = new VoxelCursor(map);
            return TrySampleCellValue(ref cursor, p, out distance);
        }

        /// <summary>
        /// <see cref="TrySampleRaw(ChunkMap, Vector3, out float)"/> for a caller that is already
        /// walking a small region and can keep the chunk memo alive across its whole sweep. The
        /// per-call overload above starts cold every time, which for something like a navigation
        /// column — dozens of samples inside one chunk — throws the memo away between each one.
        /// </summary>
        public static bool TrySampleRaw(ref VoxelCursor cursor, Vector3 p, out float distance)
            => TrySampleCellValue(ref cursor, p, out distance);

        /// <summary>
        /// The trilinear value alone, without the analytic cell gradient.
        /// </summary>
        /// <remarks>
        /// Same eight corners and the same seven lerps as <see cref="TrySampleCell"/>, minus the nine
        /// further lerps and the Vector3 that build the gradient. That matters because the gradient was
        /// being computed and thrown away on the hot path: one <see cref="TrySample"/> makes six
        /// <see cref="TrySampleRaw"/> calls for its central-difference stencil, and every one of them
        /// discarded a gradient. Identical output, strictly less arithmetic.
        /// </remarks>
        static bool TrySampleCellValue(ref VoxelCursor cursor, Vector3 p, out float distance)
        {
            distance = 0f;

            int x0 = (int)MathF.Floor(p.X);
            int y0 = (int)MathF.Floor(p.Y);
            int z0 = (int)MathF.Floor(p.Z);

            float tx = p.X - x0;
            float ty = p.Y - y0;
            float tz = p.Z - z0;

            Span<float> d = stackalloc float[8];
            for (int c = 0; c < 8; c++)
            {
                if (!cursor.TryGet(x0 + (c & 1), y0 + ((c >> 1) & 1), z0 + ((c >> 2) & 1), out var voxel))
                    return false;

                d[c] = voxel.Distance;
            }

            float y0z0 = Lerp(d[0], d[1], tx);
            float y1z0 = Lerp(d[2], d[3], tx);
            float y0z1 = Lerp(d[4], d[5], tx);
            float y1z1 = Lerp(d[6], d[7], tx);

            distance = Lerp(Lerp(y0z0, y1z0, ty), Lerp(y0z1, y1z1, ty), tz);
            return true;
        }

        /// <summary>
        /// Trilinear value and its exact analytical gradient inside the containing cell. Deriving
        /// both from the same eight corners avoids a cross-cell finite-difference stencil pulling
        /// saturated samples into an otherwise well-resolved steep surface.
        /// </summary>
        static bool TrySampleCell(ref VoxelCursor cursor, Vector3 p, out float distance, out Vector3 gradient)
        {
            distance = 0f;
            gradient = Vector3.Zero;

            int x0 = (int)MathF.Floor(p.X);
            int y0 = (int)MathF.Floor(p.Y);
            int z0 = (int)MathF.Floor(p.Z);

            float tx = p.X - x0;
            float ty = p.Y - y0;
            float tz = p.Z - z0;

            Span<float> d = stackalloc float[8];
            for (int c = 0; c < 8; c++)
            {
                // Corner index encodes the offset as x + 2y + 4z, the same packing the mesher uses.
                if (!cursor.TryGet(x0 + (c & 1), y0 + ((c >> 1) & 1), z0 + ((c >> 2) & 1), out var voxel))
                    return false;

                d[c] = voxel.Distance;
            }

            float y0z0 = Lerp(d[0], d[1], tx);
            float y1z0 = Lerp(d[2], d[3], tx);
            float y0z1 = Lerp(d[4], d[5], tx);
            float y1z1 = Lerp(d[6], d[7], tx);

            distance = Lerp(Lerp(y0z0, y1z0, ty), Lerp(y0z1, y1z1, ty), tz);

            float dx = Lerp(Lerp(d[1] - d[0], d[3] - d[2], ty),
                            Lerp(d[5] - d[4], d[7] - d[6], ty), tz);
            float dy = Lerp(Lerp(d[2] - d[0], d[3] - d[1], tx),
                            Lerp(d[6] - d[4], d[7] - d[5], tx), tz);
            float dz = Lerp(Lerp(d[4] - d[0], d[5] - d[1], tx),
                            Lerp(d[6] - d[2], d[7] - d[3], tx), ty);
            gradient = new Vector3(dx, dy, dz);

            return true;
        }

        /// <summary>
        /// Raw value and a corrected distance, both derived from the SAME eight corners.
        /// </summary>
        /// <remarks>
        /// For a caller that needs to know how far it may safely advance but does NOT need a surface
        /// normal — a ray march, whose normal is only wanted once, at the hit it finally reports.
        /// <para>
        /// <see cref="TrySample(ref VoxelCursor, Vector3, out FieldPoint, out float)"/> costs 56 voxel
        /// reads: eight for the cell, plus 48 more for the smoothed central-difference gradient it
        /// divides by. This costs eight. The correction uses the analytic gradient of the trilinear
        /// interpolant, which <see cref="TrySampleCell"/> already computed from those same eight corners
        /// and previously threw away.
        /// </para>
        /// <para>
        /// It is a legitimate substitute for the marching decision specifically, because
        /// <c>TerrainRaycast.SafeStep</c> takes the MINIMUM of raw and corrected, and that minimum stays
        /// safe under either gradient for the same reason it did before: where the field saturates the
        /// cell gradient collapses toward zero and inflates the corrected value, but raw is clamped
        /// there and understates the gap, so the minimum is the clamped one; where it does not
        /// saturate, the corrected value is the true distance. Do NOT reach for this where a normal is
        /// wanted — the smoothed gradient exists because the per-cell one is discontinuous at cell
        /// boundaries, which is invisible in a step length and very visible in shading and pushout.
        /// </para>
        /// </remarks>
        public static bool TrySampleCellCorrected(
            ref VoxelCursor cursor, Vector3 p, out float raw, out float corrected)
        {
            corrected = 0f;
            if (!TrySampleCell(ref cursor, p, out raw, out var cellGradient)) return false;

            float length = cellGradient.Length();

            // Same guard as TrySample: a clamped-out interior has no length to divide by. Report the
            // raw value, which is still correctly signed, so callers still see "inside".
            corrected = length <= MinGradientLength ? raw : raw / length;
            return true;
        }

        /// <summary>
        /// The field at a point as collision needs it.
        ///
        /// The stored value is NOT a distance: <see cref="ChunkGenerator"/> writes `y - heightAt`,
        /// the VERTICAL gap to the terrain height in that column. A true signed distance field has
        /// |grad d| = 1 everywhere; this one has |grad d| = 1/cos(slope), so on a slope it overstates
        /// how far the surface is by exactly that factor. Dividing by the gradient length recovers a
        /// true distance — without it, resolving a sphere until the stored value equals its radius
        /// leaves it sunk into the slope by radius * (1 - cos(slope)): 29% of the radius at 45
        /// degrees, half at 60.
        ///
        /// The gradient DIRECTION needs no correction; for `y - h(x,z)` it is already the true
        /// surface normal. Only its length was ever wrong.
        /// </summary>
        public static bool TrySample(ChunkMap map, Vector3 p, out FieldPoint point)
        {
            var cursor = new VoxelCursor(map);
            return TrySample(ref cursor, p, out point);
        }

        /// <summary>Cursor-sharing <see cref="TrySample(ChunkMap, Vector3, out FieldPoint)"/>, for a
        /// caller marching a ray or sweeping a small region.</summary>
        public static bool TrySample(ref VoxelCursor cursor, Vector3 p, out FieldPoint point)
            => TrySample(ref cursor, p, out point, out _);

        /// <summary>
        /// <see cref="TrySample(ref VoxelCursor, Vector3, out FieldPoint)"/> that also hands back the
        /// RAW stored value it had to compute anyway.
        ///
        /// For a caller that wants both — a ray march needs the corrected distance to know it has
        /// arrived and the raw one to know how far it may safely step — this is the difference
        /// between one eight-corner trilinear evaluation and two identical ones at the same point.
        /// </summary>
        public static bool TrySample(
            ref VoxelCursor cursor, Vector3 p, out FieldPoint point, out float rawDistance)
        {
            point = default;
            rawDistance = 0f;

            if (!TrySampleCell(ref cursor, p, out float raw, out var cellGradient)) return false;
            rawDistance = raw;
            if (!TryGradient(ref cursor, p, out var gradient)) return false;

            float length = gradient.Length();
            float cellLength = cellGradient.Length();

            // Clamped-out interior: no length to divide by and no direction to escape along. Report
            // the raw value (still correctly signed, so callers see "inside") and push straight up.
            if (length <= MinGradientLength)
            {
                point = new FieldPoint(raw, Vector3.UnitY, Vector3.UnitY);
                return true;
            }

            var normal = gradient / length;
            var surfaceNormal = cellLength > MinGradientLength ? cellGradient / cellLength : normal;
            point = new FieldPoint(raw / length, normal, surfaceNormal);
            return true;
        }

        /// <summary>
        /// The deepest contact across the body's sample spheres — the one that has to be resolved
        /// first, since pushing out of it may resolve the others for free. False if any sample fell
        /// in unloaded terrain.
        /// </summary>
        public static bool TryDeepestContact(ChunkMap map, in CapsuleBody body, Vector3 feet, out FieldPoint deepest)
        {
            deepest = new FieldPoint(float.MaxValue, Vector3.UnitY, Vector3.UnitY);

            // One cursor across all three sample spheres: they are a capsule's worth apart, so the
            // second and third usually land in the chunk the first already resolved.
            var cursor = new VoxelCursor(map);
            return TryDeepestContact(ref cursor, body, feet, out deepest);
        }

        /// <summary>
        /// <see cref="TryDeepestContact(ChunkMap, in CapsuleBody, Vector3, out FieldPoint)"/>
        /// sharing the caller's memo. A standability check resolves the capsule up to four times in
        /// one column and each pass used to start cold, so the same chunk was looked up from scratch
        /// hundreds of times for one cell.
        /// </summary>
        public static bool TryDeepestContact(
            ref VoxelCursor cursor, in CapsuleBody body, Vector3 feet, out FieldPoint deepest)
        {
            deepest = new FieldPoint(float.MaxValue, Vector3.UnitY, Vector3.UnitY);

            for (int i = 0; i < CapsuleBody.SampleCount; i++)
            {
                if (!TrySample(ref cursor, body.SampleCenter(feet, i), out var point)) return false;
                if (point.Distance < deepest.Distance) deepest = point;
            }

            return true;
        }

        /// <summary>Smoothed central difference used for stable pushout at cell boundaries.</summary>
        static bool TryGradient(ref VoxelCursor cursor, Vector3 p, out Vector3 gradient)
        {
            gradient = Vector3.Zero;

            if (!TryAxisDifference(ref cursor, p, Vector3.UnitX, out float dx)) return false;
            if (!TryAxisDifference(ref cursor, p, Vector3.UnitY, out float dy)) return false;
            if (!TryAxisDifference(ref cursor, p, Vector3.UnitZ, out float dz)) return false;

            gradient = new Vector3(dx, dy, dz);
            return true;
        }

        static bool TryAxisDifference(ref VoxelCursor cursor, Vector3 p, Vector3 axis, out float difference)
        {
            difference = 0f;

            if (!TrySampleRaw(ref cursor, p + axis * GradientStep, out float ahead)) return false;
            if (!TrySampleRaw(ref cursor, p - axis * GradientStep, out float behind)) return false;

            difference = (ahead - behind) / (2f * GradientStep);
            return true;
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
