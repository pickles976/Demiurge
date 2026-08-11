using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Where the world is being looked at from, in the only terms LOD selection actually needs: an eye,
    /// a basis, how wide the lens is, and how tall the image is in pixels.
    ///
    /// THIS TYPE EXISTS BECAUSE DISTANCE WAS NEVER THE REAL RULE. The radii it replaced — TerrainLod's
    /// old `SplitWithin = {0, 112, 224}` — are exactly 56x the cell size of the level they gate, and
    /// 56 m per metre of cell is what a constant 12.8 px of error works out to at the hip field of view
    /// on a 1080-tall back buffer. The table was already a screen-space error budget. It just had the
    /// field of view baked into its constants, so when <c>FirstPersonCamera</c> narrows the view from
    /// 74 deg to 56 deg and then divides the tangent by the optic's magnification, the real error went
    /// up by that whole factor and nothing recomputed. At 4x that is 5x the budget — which is why a
    /// one-voxel <c>structures/</c> wall, unsampled by LOD 1's stride of 2, vanishes at 112 m through
    /// a scope and not at all through iron sights.
    ///
    /// Stating the budget in PIXELS rather than metres makes zoom arithmetic instead of a branch, and
    /// makes "off screen" mean zero error rather than a separate culling rule bolted alongside.
    ///
    /// The frustum is deliberately WIDENED at construction (see <c>marginDegrees</c>). Selection runs
    /// on view change, and a box sitting exactly on the frustum edge would otherwise split and merge
    /// on every few degrees of mouse movement. Hysteresis belongs in the geometry, where it costs a
    /// handful of extra boxes, rather than in a scheduler that would delay genuine refinement too.
    /// </summary>
    public readonly struct TerrainView
    {
        /// <summary>The eye. Distances and the unconditional bubble are measured from here.</summary>
        public readonly Vector3 Origin;

        /// <summary>Normalized view axis, and the tangent of half the vertical field of view. Public
        /// because the caller reselects on view CHANGE, and these two are what changed.</summary>
        public readonly Vector3 Forward;
        public readonly float TanHalfFovY;

        /// <summary>
        /// Pixels of error per world unit of feature size, at one world unit of distance. Divided by
        /// distance to get the error a feature actually subtends, which is the whole of the model.
        /// </summary>
        readonly float errorScale;

        readonly Vector4 near, left, right, bottom, top;

        /// <summary>False for <see cref="Everywhere"/>, whose planes are meaningless.</summary>
        readonly bool bounded;

        public TerrainView(
            Vector3 origin,
            Vector3 forward,
            Vector3 up,
            float tanHalfFovY,
            float aspect,
            float viewportHeight,
            float marginDegrees)
        {
            Origin = origin;
            TanHalfFovY = tanHalfFovY;
            errorScale = viewportHeight * 0.5f / MathF.Max(tanHalfFovY, 1e-4f);

            var f = SafeNormalize(forward, Vector3.UnitZ);
            Forward = f;
            var r = Vector3.Cross(f, up);
            if (r.LengthSquared() < 1e-8f) r = Vector3.Cross(f, Vector3.UnitX);
            r = SafeNormalize(r, Vector3.UnitX);
            var u = Vector3.Cross(r, f);

            // Widen by the margin in ANGLE, not in tangent: adding to a tangent is a much bigger
            // widening at a narrow field of view than at a broad one, which is backwards — the narrow
            // case is the one that must not thrash.
            float margin = marginDegrees * MathF.PI / 180f;
            float tanY = WidenedTangent(tanHalfFovY, margin);
            float tanX = WidenedTangent(tanHalfFovY * MathF.Max(aspect, 1e-3f), margin);

            near = Plane(f, origin);
            left = Plane(Vector3.Normalize(r + f * tanX), origin);
            right = Plane(Vector3.Normalize(-r + f * tanX), origin);
            bottom = Plane(Vector3.Normalize(u + f * tanY), origin);
            top = Plane(Vector3.Normalize(-u + f * tanY), origin);
            bounded = true;
        }

        /// <summary>
        /// A view with an eye and an error scale but no facing — everything is "in front of" it.
        ///
        /// For the frames before a local player exists, where refusing to select any terrain would
        /// leave the world blank, and for tests that only care about the distance half of the model.
        /// </summary>
        public static TerrainView Everywhere(Vector3 origin, float tanHalfFovY, float viewportHeight)
            => new(origin, tanHalfFovY, viewportHeight * 0.5f / MathF.Max(tanHalfFovY, 1e-4f));

        TerrainView(Vector3 origin, float tanHalfFovY, float errorScale)
        {
            Origin = origin;
            Forward = Vector3.UnitZ;
            TanHalfFovY = tanHalfFovY;
            this.errorScale = errorScale;
            near = left = right = bottom = top = default;
            bounded = false;
        }

        /// <summary>
        /// How many pixels a feature of <paramref name="featureSize"/> world units subtends at
        /// <paramref name="distance"/>. An eye inside the box gets the maximum, so it always refines.
        /// </summary>
        public float PixelError(float featureSize, float distance)
            => distance <= 1e-3f ? float.MaxValue : featureSize * errorScale / distance;

        /// <summary>Whether an axis-aligned box is at least partly inside the widened frustum.</summary>
        public bool Intersects(Vector3 min, Vector3 max)
            => !bounded
            || (!Outside(near, min, max)
             && !Outside(left, min, max)
             && !Outside(right, min, max)
             && !Outside(bottom, min, max)
             && !Outside(top, min, max));

        /// <summary>Behind a plane iff the box's most-positive vertex along its normal is behind it.</summary>
        static bool Outside(in Vector4 plane, Vector3 min, Vector3 max)
        {
            var n = new Vector3(plane.X, plane.Y, plane.Z);
            var far = new Vector3(
                n.X >= 0f ? max.X : min.X,
                n.Y >= 0f ? max.Y : min.Y,
                n.Z >= 0f ? max.Z : min.Z);

            return Vector3.Dot(n, far) + plane.W < 0f;
        }

        static Vector4 Plane(Vector3 normal, Vector3 through)
            => new(normal, -Vector3.Dot(normal, through));

        /// <summary>Clamped short of 90 degrees, past which the tangent flips sign and the plane inverts.</summary>
        static float WidenedTangent(float tangent, float margin)
            => MathF.Tan(MathF.Min(MathF.Atan(MathF.Max(tangent, 1e-4f)) + margin, 1.55f));

        static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
            => v.LengthSquared() < 1e-8f ? fallback : Vector3.Normalize(v);
    }
}
