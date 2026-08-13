using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// What a shovel is doing this click. One request type covers both because they are one action
    /// with the sign flipped: same reach, same rate limit, same grid, same brush, same number of
    /// clicks per voxel — only the CSG operator and which side of the surface it lands on differ.
    /// Wire protocol (rides inside PlayerDigData) — append-only.
    /// </summary>
    public enum TerrainAction : byte
    {
        /// <summary>Take the voxel behind the surface out. The default, so an old-shaped request and
        /// every NPC dig still mean exactly what they used to.</summary>
        Dig = 0,
        /// <summary>Put a voxel in front of the surface in.</summary>
        Place = 1,
    }

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

        /// <summary>
        /// Navigation excavation is laid out on one-metre cells. A half-metre brush removes the
        /// committed capsule corridor without nibbling its neighbouring tread or a squadmate's
        /// parapet; freehand player digs retain <see cref="BiteRadius"/>.
        /// </summary>
        public const float PlannedBiteRadius = 0.5f;

        /// <summary>How many accepted dig edits should amount to one old full-strength bite. Shared
        /// with placement, which is the same brush run the other way — two clicks to take a voxel
        /// out, two to put one back.</summary>
        public const int ClicksPerVoxel = 2;

        /// <summary>Strength of one accepted dig relative to the old full bite.</summary>
        public const float BiteStrength = 1f / ClicksPerVoxel;

        /// <summary>
        /// Planned cuts use a smaller volume and a full-strength sample change. This keeps their
        /// one-metre corridor precise without doubling the number of rate-limited shovel cycles;
        /// player freehand digging and placement retain <see cref="BiteStrength"/>.
        /// </summary>
        public const float PlannedBiteStrength = 1f;

        /// <summary>
        /// Whether the shovel can build as well as dig.
        ///
        /// Off. The mechanism is intact and tested — <see cref="TerrainAction"/>, the placement
        /// target, the brush, the server gate — this is the one switch that decides whether players
        /// are offered it. Read by BOTH ends deliberately: the client must not highlight or request
        /// what the server would refuse, or the outline sits on a voxel that never appears.
        /// </summary>
        public const bool PlacementEnabled = false;

        /// <summary>
        /// What a shovel builds with. Fixed rather than carried on the request: there is no way for
        /// a player to CHOOSE a material yet, and a block type off the wire would be a client
        /// deciding what the world is made of. When a material picker exists this becomes a field on
        /// PlayerDigData that the server validates against what the player is carrying.
        /// </summary>
        public const BlockType PlacedBlock = BlockType.BlockType_Sandbags;

        /// <summary>Held digging cadence. The client sends at this rate; the server enforces it.</summary>
        public const float HoldHz = 2f;

        public const uint TicksPerDig = NetworkConfig.TickRate / 2;

        /// <summary>The radius packed as an extent, since an edit carries one vector for both
        /// primitives — a half-extent for a box, a radius in X for a sphere.</summary>
        public static readonly Vector3 Bite = new(BiteRadius, BiteRadius, BiteRadius);
        public static readonly Vector3 PlannedBite = new(
            PlannedBiteRadius,
            PlannedBiteRadius,
            PlannedBiteRadius);

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

        /// <summary>
        /// The grid point a placement fills, given the same hit and normal a dig would use.
        ///
        /// Literally the inverse: step half a voxel OUT along the normal instead of in, so the brush
        /// lands on the air sample against the surface rather than the solid one behind it. Rounding
        /// after the step is what keeps the two on one grid — a placement that landed between
        /// samples would build a shell that no dig could line up with.
        /// </summary>
        public static Vector3 PlacementVoxel(Vector3 hitPoint, Vector3 normal)
        {
            var outside = hitPoint + Vector3.Normalize(normal) * 0.5f;
            return new Vector3(MathF.Round(outside.X), MathF.Round(outside.Y), MathF.Round(outside.Z));
        }

        /// <summary>The grid point one action targets. One function so the client's highlight and the
        /// server's edit cannot pick different voxels for the same click.</summary>
        public static Vector3 TargetVoxel(Vector3 hitPoint, Vector3 normal, TerrainAction action)
            => action == TerrainAction.Place
                ? PlacementVoxel(hitPoint, normal)
                : TargetVoxel(hitPoint, normal);

        /// <summary>
        /// Whether a placement at <paramref name="target"/> would be built through the player
        /// standing at <paramref name="feet"/>.
        ///
        /// Digging never needed this — you cannot dig the air you are standing in — but building
        /// does, and it is the one placement rule that cannot be left to taste: the brush lands
        /// roughly an arm away at chest height, which is exactly where the body is when you look
        /// down. Without it the ordinary way to place a floor is to entomb yourself in it.
        ///
        /// Checked against the capsule the movement step actually collides with, widened by the
        /// brush radius, so "it would have pushed me" and "it is refused" are the same volume.
        /// Only the requesting player: another actor standing where you build gets shoved by the
        /// collision step, which is survivable, whereas scanning every actor per click is not free
        /// and is a rule nobody can see.
        /// </summary>
        public static bool WouldEncasePlayer(Vector3 feet, Vector3 target)
        {
            var body = PlayerMovement.Body;
            float reach = body.Radius + BiteRadius;

            float dx = target.X - feet.X;
            float dz = target.Z - feet.Z;
            if (dx * dx + dz * dz > reach * reach) return false;

            return target.Y > feet.Y - BiteRadius
                && target.Y < feet.Y + body.Height + BiteRadius;
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
