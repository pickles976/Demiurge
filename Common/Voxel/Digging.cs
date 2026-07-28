using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Turning "where the player is looking" into "which voxel comes out".
    ///
    /// In Common because BOTH ends have to agree on it. The client highlights a voxel and asks for
    /// it; the server decides whether that request is legal. If the two derived the target
    /// differently, the block that lit up and the block that vanished could be different ones.
    /// </summary>
    public static class Digging
    {
        /// <summary>
        /// How far a player can reach to dig, in metres. Also the range the server re-checks: the
        /// aim ray itself runs much further, so without this you could dig a hillside from across
        /// the valley just by looking at it.
        /// </summary>
        public const float Reach = 4.5f;

        /// <summary>
        /// Radius of one dig, as a SPHERE rather than a box.
        ///
        /// A box brush leaves what it is made of: six axis-aligned faces and twelve right-angled
        /// edges, all aligned to the voxel grid. Those flat facets meet the old surface at glancing
        /// angles, which is where long thin triangles come from, and a hollow made of them reads as
        /// milled rather than dug. A sphere has no faces and no edges — its field is smooth
        /// everywhere — so a bite out of it is a bowl.
        ///
        /// 0.7 rather than 0.5, which would be the sphere inscribed in one voxel and removes only
        /// half its volume. At 0.7 the target sample goes firmly to air (+0.7) and its six face
        /// neighbours land at -0.3, so the new surface sits a third of the way into them instead of
        /// stopping dead at the cell wall.
        /// </summary>
        public const float BiteRadius = 0.7f;

        /// <summary>How many accepted dig edits should amount to one old full-strength bite.</summary>
        public const int ClicksPerVoxel = 2;

        /// <summary>Strength of one accepted dig relative to the old full bite.</summary>
        public const float BiteStrength = 1f / ClicksPerVoxel;

        /// <summary>Held digging cadence. The client sends at this rate; the server enforces it.</summary>
        public const float HoldHz = 2f;

        public const uint TicksPerDig = NetworkConfig.TickRate / 2;

        /// <summary>The radius packed as an extent, since an edit carries one vector for both
        /// primitives — a half-extent for a box, a radius in X for a sphere.</summary>
        public static readonly Vector3 Bite = new(BiteRadius, BiteRadius, BiteRadius);

        /// <summary>
        /// Where a player looks and reaches FROM, above their feet. Matches
        /// FirstPersonCameraScript.EyeHeight, so sharing the number is
        /// what keeps "what the crosshair covers" and "what the hand can get to" the same question.
        /// </summary>
        public const float EyeHeight = 1.55f;

        /// <summary>
        /// Slack on the server's range check. The client aims from its predicted position a frame or
        /// two ahead of the server's, so an exactly-Reach test would reject legitimate digs at the
        /// edge for no reason a player could see.
        /// </summary>
        public const float ReachTolerance = 1.5f;

        /// <summary>
        /// The grid point a dig removes, given where a ray met the surface and the outward normal
        /// there.
        ///
        /// Steps half a voxel back ALONG the normal — into the solid — before rounding. The hit
        /// point sits exactly on the isosurface, so rounding it directly is a coin toss between the
        /// solid voxel and the air one in front of it, and losing that toss digs nothing at all
        /// while the highlight sat on something diggable.
        /// </summary>
        public static Vector3 TargetVoxel(Vector3 hitPoint, Vector3 normal)
        {
            var inside = hitPoint - Vector3.Normalize(normal) * 0.5f;
            return new Vector3(MathF.Round(inside.X), MathF.Round(inside.Y), MathF.Round(inside.Z));
        }

        /// <summary>Where a player's reach is centred, given where they are standing.</summary>
        public static Vector3 Eye(Vector3 feet) => feet + new Vector3(0f, EyeHeight, 0f);

        /// <summary>
        /// The footprint a dig would leave on the surface, as a closed ring of world-space points.
        /// Returns how many were written, or 0 if there is nothing to draw.
        ///
        /// This is the INTERSECTION of the bite sphere with the surface, not a flat disc laid on
        /// top. The target sits some depth h below the surface, so a sphere of radius r cuts a
        /// circle of radius sqrt(r^2 - h^2) — bite fully buried and there is no footprint at all,
        /// which is the honest thing to show for a dig that opens nothing.
        ///
        /// Each point is then dropped onto the field by Newton steps along the gradient, so the ring
        /// bends over a slope and curls into a hollow instead of hovering across it. That is what
        /// makes it read as a brush touching the ground. Two steps is plenty: the field's distance
        /// is gradient-corrected, so a single step lands exactly on a flat surface and the second
        /// only pays for curvature.
        /// </summary>
        public static int ProjectedRing(ChunkMap map, Vector3 target, Vector3 normal, Span<Vector3> into)
        {
            if (into.Length == 0) return 0;
            if (normal.LengthSquared() < 1e-6f) return 0;
            normal = Vector3.Normalize(normal);

            // How deep the target sits, measured through the field rather than assumed: on a slope
            // the rounding in TargetVoxel can leave it anywhere in the voxel.
            if (!TerrainCollision.TrySample(map, target, out var at)) return 0;

            float depth = -at.Distance;
            float radius = BiteRadius * BiteRadius - depth * depth;
            if (radius <= 0f) return 0;                 // bite never breaks the surface
            radius = MathF.Sqrt(radius);

            // The point on the surface directly above the target is where the circle is centred.
            var centre = target + normal * depth;

            // Any two axes across the normal. Cross with whichever world axis it is least aligned
            // to, so the pair never degenerates on a wall or a ceiling.
            var reference = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
            var u = Vector3.Normalize(Vector3.Cross(normal, reference));
            var v = Vector3.Cross(normal, u);

            for (int i = 0; i < into.Length; i++)
            {
                float angle = i * (MathF.PI * 2f / into.Length);
                var point = centre + radius * (u * MathF.Cos(angle) + v * MathF.Sin(angle));

                for (int step = 0; step < 2; step++)
                    if (TerrainCollision.TrySample(map, point, out var field))
                        point -= field.Normal * field.Distance;

                into[i] = point;
            }

            return into.Length;
        }

        /// <summary>
        /// Whether a dig at <paramref name="target"/> is close enough to a player standing at
        /// <paramref name="feet"/> to be legal. Measured from the EYE, the same point the client
        /// casts from, so the server accepts exactly the sphere the client offered — measuring from
        /// somewhere else would reject digs at the edge that looked perfectly reachable.
        /// </summary>
        public static bool InReach(Vector3 feet, Vector3 target)
        {
            float limit = Reach + ReachTolerance;
            return Vector3.DistanceSquared(Eye(feet), target) <= limit * limit;
        }
    }
}
