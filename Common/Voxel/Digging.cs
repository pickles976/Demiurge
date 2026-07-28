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
