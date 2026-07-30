using System.Numerics;

namespace Demiurge;

/// <summary>
/// The shared terrain-to-navigation contract. Search and path following must never invent their
/// own definition of walkable terrain; every query comes through these collision-derived checks.
/// </summary>
public static class NavTraversal
{
    public const int MaximumTraverseCellDelta = 2;
    public const int MaximumFallCells = 16;

    private const float SurfaceEpsilon = 1e-5f;
    private const float ContactTolerance = 0.015f;
    private const float WalkValidationRise =
        PlayerMovement.GroundSnapDistance;
    private static readonly int MaximumWalkValidationTicks =
        (int)MathF.Ceiling(
            2f / PlayerMovement.WalkSpeed / NetworkConfig.FixedDt);
    private static readonly float MaximumRisePerMetre =
        MathF.Tan(PlayerMovement.MaxSlopeDegrees * MathF.PI / 180f);
    private static readonly int MaximumJumpTicks =
        (int)MathF.Ceiling(
            (2f * PlayerMovement.JumpSpeed / PlayerMovement.Gravity + 0.5f)
            * NetworkConfig.TickRate);

    /// <summary>
    /// Resolves the upward SDF crossing in a cell and verifies that the complete player capsule can
    /// rest there with sufficient headroom and a movement-legal surface normal.
    /// </summary>
    public static bool Standable(
        ChunkMap map,
        int x,
        int y,
        int z,
        out float surfaceY)
        => StandableAt(map, x + 0.5f, y, z + 0.5f, out surfaceY);

    public static bool TryFindStandable(
        ChunkMap map,
        int x,
        int z,
        int aroundY,
        int below,
        int above,
        out NavCell cell,
        out float surfaceY)
    {
        cell = default;
        surfaceY = 0f;
        int maximumOffset = Math.Max(below, above);
        for (int offset = 0; offset <= maximumOffset; offset++)
        {
            if (offset <= above
                && TryAt(aroundY + offset, out cell, out surfaceY))
                return true;
            if (offset != 0
                && offset <= below
                && TryAt(aroundY - offset, out cell, out surfaceY))
                return true;
        }
        return false;

        bool TryAt(int y, out NavCell found, out float height)
        {
            found = default;
            height = 0f;
            if (y < ChunkConstants.WorldMinY
                || y >= ChunkConstants.WorldMaxY - 1
                || !Standable(map, x, y, z, out height))
                return false;
            found = new NavCell(x, y, z);
            return true;
        }
    }

    /// <summary>
    /// Finds a capsule-valid cell near an authored or procedurally spread point. Runtime formation
    /// offsets can land on a steep SDF sample even when the nearby spawn area is valid; failing the
    /// entire path request in that case makes an NPC appear permanently idle.
    /// </summary>
    public static bool TryFindNearestStandable(
        ChunkMap map,
        Vector3 position,
        int horizontalRadius,
        out NavCell cell)
    {
        if (horizontalRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalRadius));

        int centreX = (int)MathF.Floor(position.X);
        int centreZ = (int)MathF.Floor(position.Z);
        int centreY = (int)MathF.Floor(position.Y);
        for (int radius = 0; radius <= horizontalRadius; radius++)
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius)
                        continue;
                    int x = centreX + dx;
                    int z = centreZ + dz;
                    if (TryFindStandable(
                            map,
                            x,
                            z,
                            centreY,
                            below: 6,
                            above: 6,
                            out cell,
                            out _))
                        return true;

                    if (SurfaceQuery.HighestSurfaceY(map, x, z) is not { } surfaceY
                        || !TryFindStandable(
                            map,
                            x,
                            z,
                            (int)MathF.Floor(surfaceY),
                            below: 2,
                            above: 2,
                            out cell,
                            out _))
                        continue;
                    return true;
                }

        cell = default;
        return false;
    }

    /// <summary>
    /// A continuous walk edge between adjacent columns. Midpoint validation rejects narrow gaps and
    /// endpoint slope limits reject ledges steeper than the movement solver can climb.
    /// </summary>
    public static bool TryStep(
        ChunkMap map,
        NavCell from,
        NavCell to,
        out float cost)
    {
        cost = NavCosts.Inf;
        int dx = to.X - from.X;
        int dz = to.Z - from.Z;
        if ((dx == 0 && dz == 0) || Math.Abs(dx) > 1 || Math.Abs(dz) > 1)
            return false;
        if (!Standable(map, from.X, from.Y, from.Z, out float fromY)
            || !Standable(map, to.X, to.Y, to.Z, out float toY))
            return false;

        float horizontal = MathF.Sqrt(dx * dx + dz * dz);
        if (MathF.Abs(toY - fromY)
            > MaximumRisePerMetre * horizontal + PlayerMovement.GroundSnapDistance)
            return false;

        float middleX = (from.X + to.X + 1f) * 0.5f;
        float middleZ = (from.Z + to.Z + 1f) * 0.5f;
        int middleCellY = (int)MathF.Floor((fromY + toY) * 0.5f);
        if (!TryStandableAtNear(
                map,
                middleX,
                middleZ,
                middleCellY,
                MaximumTraverseCellDelta,
                out float middleY))
            return false;

        float firstRise = MathF.Abs(middleY - fromY);
        float secondRise = MathF.Abs(toY - middleY);
        float halfHorizontal = horizontal * 0.5f;
        if (firstRise > MaximumRisePerMetre * halfHorizontal + PlayerMovement.GroundSnapDistance
            || secondRise > MaximumRisePerMetre * halfHorizontal + PlayerMovement.GroundSnapDistance)
            return false;

        float distance = MathF.Sqrt(horizontal * horizontal + (toY - fromY) * (toY - fromY));
        cost = distance * NavCosts.WalkOneMetre;
        return true;
    }

    /// <summary>
    /// Cheap height gate for the uncommon ascent where endpoint geometry can make a sharp ledge
    /// look like a legal slope. Call <see cref="CanWalkEdge"/> only when this returns true.
    /// </summary>
    public static bool NeedsWalkValidation(
        ChunkMap map,
        NavCell from,
        NavCell to)
        => Standable(map, from.X, from.Y, from.Z, out float fromY)
           && Standable(map, to.X, to.Y, to.Z, out float toY)
           && toY - fromY > WalkValidationRise;

    /// <summary>
    /// Runs a short no-jump traversal with the authoritative movement solver. This distinguishes a
    /// continuous steep slope from a block lip whose interpolated endpoints pass geometric tests
    /// but whose capsule collision prevents walking onto it.
    /// </summary>
    public static bool CanWalkAscent(
        ChunkMap map,
        NavCell from,
        NavCell to)
        => CanWalkEdge(map, from, to);

    /// <summary>
    /// Runs a short no-jump traversal with the authoritative movement solver. In addition to sharp
    /// ascents, this is used to validate diagonal travel along narrow bridges where the two
    /// cardinal cell centres can be over open air even though the capsule's actual diagonal line is
    /// fully supported.
    /// </summary>
    public static bool CanWalkEdge(
        ChunkMap map,
        NavCell from,
        NavCell to)
    {
        int dx = to.X - from.X;
        int dz = to.Z - from.Z;
        if ((dx == 0 && dz == 0) || Math.Abs(dx) > 1 || Math.Abs(dz) > 1)
            return false;
        if (!Standable(map, to.X, to.Y, to.Z, out float targetY))
            return false;

        var state = new MoveState
        {
            Position = Position(map, from),
            Velocity = Vector3.Zero,
            Grounded = true,
        };
        Vector3 target = Position(map, to);
        Vector3 intent = Vector3.Normalize(new Vector3(dx, 0f, dz));
        float arrivalRadiusSquared = 0.55f * 0.55f;
        float verticalTolerance =
            PlayerMovement.GroundSnapDistance + PlayerMovement.SkinWidth + 0.15f;

        for (int tick = 0; tick < MaximumWalkValidationTicks; tick++)
        {
            PlayerMovement.Step(
                map,
                ref state,
                intent,
                PlayerStateFlags.Moving,
                NetworkConfig.FixedDt);
            float horizontalDistanceSquared =
                (state.Position.X - target.X) * (state.Position.X - target.X)
                + (state.Position.Z - target.Z) * (state.Position.Z - target.Z);
            if (horizontalDistanceSquared <= arrivalRadiusSquared
                && MathF.Abs(state.Position.Y - targetY) <= verticalTolerance)
                return true;
        }

        return false;
    }

    public static Vector3 Position(ChunkMap map, NavCell cell)
    {
        if (!Standable(map, cell.X, cell.Y, cell.Z, out float surfaceY))
            throw new ArgumentException($"Navigation cell {cell} is not standable", nameof(cell));
        return new Vector3(cell.X + 0.5f, surfaceY, cell.Z + 0.5f);
    }

    /// <summary>
    /// Simulates a standing jump with the real fixed-timestep movement solver. This is deliberately
    /// used only when the ordinary edge in this direction is blocked, keeping A* costs bounded while
    /// guaranteeing that any returned landing is one the authoritative capsule can reproduce.
    /// </summary>
    public static bool TryJump(
        ChunkMap map,
        NavCell from,
        int dx,
        int dz,
        out NavCell landing,
        out float cost)
    {
        landing = default;
        cost = NavCosts.Inf;
        if (Math.Abs(dx) + Math.Abs(dz) != 1
            || !Standable(map, from.X, from.Y, from.Z, out _))
            return false;

        var state = new MoveState
        {
            Position = Position(map, from),
            Velocity = Vector3.Zero,
            Grounded = true,
        };
        var intent = Vector3.Normalize(new Vector3(dx, 0f, dz));
        bool becameAirborne = false;

        for (int tick = 0; tick < MaximumJumpTicks; tick++)
        {
            var flags = PlayerStateFlags.Moving;
            if (tick == 0) flags |= PlayerStateFlags.Jumping;
            PlayerMovement.Step(map, ref state, intent, flags, NetworkConfig.FixedDt);
            becameAirborne |= !state.Grounded;
            if (!becameAirborne || !state.Grounded) continue;

            int x = (int)MathF.Floor(state.Position.X);
            int z = (int)MathF.Floor(state.Position.Z);
            if (!TryFindStandable(
                    map,
                    x,
                    z,
                    (int)MathF.Floor(state.Position.Y),
                    below: 2,
                    above: 2,
                    out landing,
                    out _)
                || landing == from)
                return false;

            cost = (tick + 1) * NetworkConfig.FixedDt;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the first soil voxel blocking a cardinal move. This deliberately returns a frontier
    /// action rather than pretending the terrain has already been removed: the server performs one
    /// real shovel bite, increments the terrain version, and then plans against the resulting field.
    /// Repeating that loop naturally clears both the lower and upper parts of a capsule-sized
    /// passage while stone remains an absolute boundary.
    /// </summary>
    public static bool TryDig(
        ChunkMap map,
        NavCell from,
        int dx,
        int dz,
        out Vector3 target,
        out float cost)
    {
        target = default;
        cost = NavCosts.Inf;
        if (Math.Abs(dx) + Math.Abs(dz) != 1
            || !Standable(map, from.X, from.Y, from.Z, out _))
            return false;

        Vector3 feet = Position(map, from);
        Vector3 direction = Vector3.Normalize(new Vector3(dx, 0f, dz));

        // A substantially higher standable surface in the adjacent column means this is the side
        // of a pit or embankment. Aim upward first so repeated frontier replans cut a rising series
        // of bites rather than a level tunnel under the surface.
        if (TryFindHigherSurface(1, out var upper)
            || TryFindHigherSurface(2, out upper))
        {
            Vector3 risingDirection = Vector3.Normalize(
                new Vector3(dx, 0.75f, dz));
            Vector3 risingOrigin = PlayerMovement.Body.SampleCenter(feet, 1);
            if (TryDigTargetAlongRay(
                    map,
                    risingOrigin,
                    risingDirection,
                    direction,
                    out target))
            {
                cost = NavCosts.DigOneVoxel;
                return true;
            }
        }

        // Probe the capsule axis, low to high. Removing the lowest blocker first avoids carving a
        // decorative hole above an obstruction the actor still cannot walk through.
        for (int i = 0; i < CapsuleBody.SampleCount; i++)
        {
            Vector3 origin = PlayerMovement.Body.SampleCenter(feet, i);
            if (!TryDigTargetAlongRay(map, origin, direction, direction, out target))
                continue;
            cost = NavCosts.DigOneVoxel;
            return true;
        }

        return false;

        bool TryFindHigherSurface(int steps, out SurfaceQuery.SurfaceHit upper)
        {
            upper = default;
            var surface = SurfaceQuery.HighestSurface(
                map,
                from.X + dx * steps,
                from.Z + dz * steps);
            if (surface is not { } found
                || found.Y <= feet.Y + MaximumTraverseCellDelta
                || !IsSoil(found.Material))
                return false;
            upper = found;
            return true;
        }
    }

    private static bool TryDigTargetAlongRay(
        ChunkMap map,
        Vector3 origin,
        Vector3 rayDirection,
        Vector3 inwardDirection,
        out Vector3 target)
    {
        target = default;
        if (TerrainRaycast.Cast(map, origin, rayDirection, 1.75f) is not { } hit)
            return false;

        Vector3 voxelTarget = Digging.TargetVoxel(hit.Point, hit.Normal);
        if (!TryVoxel(map, voxelTarget, out var surfaceVoxel))
            return false;
        if (surfaceVoxel.Distance < 0f)
        {
            // A solid rock sample is an absolute boundary even if soil happens to sit behind it.
            // SubtractSoil cannot change it, so accepting the edge would replan forever.
            if (!IsSoil(surfaceVoxel))
                return false;
        }
        else
        {
            // An exact isosurface may round to its zero-density (air-labelled) grid point. Inspect
            // one point inward before rejecting it, but keep the brush centred on the surface.
            Vector3 inward = voxelTarget + inwardDirection;
            if (!IsSolidSoil(map, inward))
                return false;
        }

        target = voxelTarget;
        return true;
    }

    private static bool IsSolidSoil(ChunkMap map, Vector3 target)
        => TryVoxel(map, target, out var voxel)
           && voxel.Distance < 0f
           && IsSoil(voxel);

    private static bool TryVoxel(ChunkMap map, Vector3 target, out Voxel voxel)
        => map.TryGetVoxel(
            (int)MathF.Round(target.X),
            (int)MathF.Round(target.Y),
            (int)MathF.Round(target.Z),
            out voxel);

    private static bool IsSoil(Voxel voxel)
        => IsSoil(voxel.Material);

    private static bool IsSoil(BlockType material)
        => material is (
            BlockType.BlockType_Grass
            or BlockType.BlockType_Dirt);

    private static bool TryStandableAtNear(
        ChunkMap map,
        float x,
        float z,
        int aroundY,
        int range,
        out float surfaceY)
    {
        surfaceY = 0f;
        for (int offset = 0; offset <= range; offset++)
        {
            if (StandableAt(map, x, aroundY + offset, z, out surfaceY))
                return true;
            if (offset != 0 && StandableAt(map, x, aroundY - offset, z, out surfaceY))
                return true;
        }
        return false;
    }

    private static bool StandableAt(
        ChunkMap map,
        float x,
        int y,
        float z,
        out float surfaceY)
    {
        surfaceY = 0f;
        if (y < ChunkConstants.WorldMinY || y >= ChunkConstants.WorldMaxY - 1)
            return false;

        if (!TerrainCollision.TrySampleRaw(map, new Vector3(x, y, z), out float below)
            || !TerrainCollision.TrySampleRaw(map, new Vector3(x, y + 1f, z), out float above)
            || below >= -SurfaceEpsilon
            || above < 0f)
            return false;

        float denominator = above - below;
        if (denominator <= SurfaceEpsilon) return false;
        float fieldSurfaceY = y + Math.Clamp(-below / denominator, 0f, 1f);
        var feet = new Vector3(x, fieldSurfaceY, z);
        float restingDistance = PlayerMovement.Body.Radius + PlayerMovement.SkinWidth;
        for (int pass = 0; pass < 4; pass++)
        {
            if (!TerrainCollision.TryDeepestContact(
                    map,
                    PlayerMovement.Body,
                    feet,
                    out var contact)
                || contact.SurfaceNormal.Y < PlayerMovement.MaxSlopeCos)
                return false;

            float depth = restingDistance - contact.Distance;
            if (depth <= ContactTolerance)
            {
                if (depth < -PlayerMovement.GroundSnapDistance) return false;
                surfaceY = feet.Y;
                return true;
            }

            // PlayerMovement resolves along the contact normal. A navigation column keeps X/Z
            // fixed, so apply the equivalent vertical lift needed to gain the same normal gap.
            if (contact.Normal.Y <= PlayerMovement.MaxSlopeCos) return false;
            feet.Y += depth / contact.Normal.Y;
        }

        return false;
    }
}
