using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// A body's contact with the field at one point: how far away the surface is, and which way is
    /// out of it. Distance is CORRECTED (see <see cref="TerrainCollision.TrySample"/>) — negative
    /// means the point is inside terrain. Normal is a unit vector pointing out.
    /// </summary>
    public readonly record struct FieldPoint(float Distance, Vector3 Normal);

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
        /// <summary>
        /// Half-width of the central difference, in voxels. Wider than the mesher's whole-voxel
        /// stencil would be smoother still, but also blunts thin features; half a voxel keeps the
        /// pushout direction continuous across cell boundaries without rounding off ledges.
        /// </summary>
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
                // Corner index encodes the offset as x + 2y + 4z, the same packing the mesher uses.
                if (!map.TryGetVoxel(x0 + (c & 1), y0 + ((c >> 1) & 1), z0 + ((c >> 2) & 1), out var voxel))
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
            point = default;

            if (!TrySampleRaw(map, p, out float raw)) return false;
            if (!TryGradient(map, p, out var gradient)) return false;

            float length = gradient.Length();

            // Clamped-out interior: no length to divide by and no direction to escape along. Report
            // the raw value (still correctly signed, so callers see "inside") and push straight up.
            if (length <= MinGradientLength)
            {
                point = new FieldPoint(raw, Vector3.UnitY);
                return true;
            }

            point = new FieldPoint(raw / length, gradient / length);
            return true;
        }

        /// <summary>
        /// The deepest contact across the body's sample spheres — the one that has to be resolved
        /// first, since pushing out of it may resolve the others for free. False if any sample fell
        /// in unloaded terrain.
        /// </summary>
        public static bool TryDeepestContact(ChunkMap map, in CapsuleBody body, Vector3 feet, out FieldPoint deepest)
        {
            deepest = new FieldPoint(float.MaxValue, Vector3.UnitY);

            for (int i = 0; i < CapsuleBody.SampleCount; i++)
            {
                if (!TrySample(map, body.SampleCenter(feet, i), out var point)) return false;
                if (point.Distance < deepest.Distance) deepest = point;
            }

            return true;
        }

        /// <summary>Central difference of the trilinear field. Six samples, forty-eight voxel reads.</summary>
        static bool TryGradient(ChunkMap map, Vector3 p, out Vector3 gradient)
        {
            gradient = Vector3.Zero;

            if (!TryAxisDifference(map, p, Vector3.UnitX, out float dx)) return false;
            if (!TryAxisDifference(map, p, Vector3.UnitY, out float dy)) return false;
            if (!TryAxisDifference(map, p, Vector3.UnitZ, out float dz)) return false;

            gradient = new Vector3(dx, dy, dz);
            return true;
        }

        static bool TryAxisDifference(ChunkMap map, Vector3 p, Vector3 axis, out float difference)
        {
            difference = 0f;

            if (!TrySampleRaw(map, p + axis * GradientStep, out float ahead)) return false;
            if (!TrySampleRaw(map, p - axis * GradientStep, out float behind)) return false;

            difference = (ahead - behind) / (2f * GradientStep);
            return true;
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
