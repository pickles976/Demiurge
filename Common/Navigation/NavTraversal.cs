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
