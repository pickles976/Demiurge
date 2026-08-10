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
    /// <summary>
    /// Simulated ticks a jump gets to leave the ground. The impulse is applied on the first step, so
    /// a jump that is still grounded after this never happened — it was blocked by headroom — and
    /// the remaining ticks can only re-confirm that.
    /// </summary>
    private const int MaximumJumpLaunchTicks = 3;

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
    {
        var cursor = new VoxelCursor(map);
        return Standable(ref cursor, x, y, z, out surfaceY);
    }

    /// <summary>
    /// The cursor-sharing form. Every one of these checks resolves the player capsule against the
    /// field several times over, and a caller that asks about a handful of neighbouring cells —
    /// A* expanding a node, cover counting escape routes — is reading the same one or two chunks
    /// throughout. Handing the memo down instead of starting cold per sample is the difference,
    /// and the arithmetic is untouched.
    /// </summary>
    public static bool Standable(
        ref VoxelCursor cursor,
        int x,
        int y,
        int z,
        out float surfaceY)
        => StandableAt(ref cursor, x + 0.5f, y, z + 0.5f, out surfaceY);

    /// <summary>
    /// The memoized form. Same answer as the others — this is the one worth reaching for, because
    /// a caller that probes a neighbourhood asks about the same cells many times over and this is
    /// the only overload that notices.
    /// </summary>
    public static bool Standable(
        NavProbeCache cache,
        int x,
        int y,
        int z,
        out float surfaceY)
    {
        if (cache.Lookup(x, y, z, out bool cached, out surfaceY)) return cached;
        bool result = StandableAt(ref cache.Cursor, x + 0.5f, y, z + 0.5f, out surfaceY);
        cache.Store(x, y, z, result, surfaceY);
        return result;
    }

    /// <summary>Memoized <see cref="TryFindStandable(ChunkMap, int, int, int, int, int, out NavCell, out float)"/>.</summary>
    public static bool TryFindStandable(
        NavProbeCache cache,
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
            if (offset <= above && TryAt(cache, x, z, aroundY + offset, out cell, out surfaceY))
                return true;
            if (offset != 0
                && offset <= below
                && TryAt(cache, x, z, aroundY - offset, out cell, out surfaceY))
                return true;
        }
        return false;

        static bool TryAt(
            NavProbeCache cache, int x, int z, int y, out NavCell found, out float height)
        {
            found = default;
            height = 0f;
            if (y < ChunkConstants.WorldMinY
                || y >= ChunkConstants.WorldMaxY - 1
                || !Standable(cache, x, y, z, out height))
                return false;
            found = new NavCell(x, y, z);
            return true;
        }
    }

    /// <summary>Memoized <see cref="TryStep(ChunkMap, NavCell, NavCell, out float)"/>.</summary>
    public static bool TryStep(
        NavProbeCache cache,
        NavCell from,
        NavCell to,
        out float cost)
    {
        cost = NavCosts.Inf;
        int dx = to.X - from.X;
        int dz = to.Z - from.Z;
        if ((dx == 0 && dz == 0) || Math.Abs(dx) > 1 || Math.Abs(dz) > 1)
            return false;
        if (!Standable(cache, from.X, from.Y, from.Z, out float fromY)
            || !Standable(cache, to.X, to.Y, to.Z, out float toY))
            return false;

        float horizontal = MathF.Sqrt(dx * dx + dz * dz);
        if (MathF.Abs(toY - fromY)
            > MaximumRisePerMetre * horizontal + PlayerMovement.GroundSnapDistance)
            return false;

        float middleX = (from.X + to.X + 1f) * 0.5f;
        float middleZ = (from.Z + to.Z + 1f) * 0.5f;
        int middleCellY = (int)MathF.Floor((fromY + toY) * 0.5f);
        if (!TryStandableAtNear(
                ref cache.Cursor,
                middleX,
                middleZ,
                middleCellY,
                MaximumTraverseCellDelta,
                out float middleY))
            return false;

        float halfHorizontal = horizontal * 0.5f;
        if (MathF.Abs(middleY - fromY)
                > MaximumRisePerMetre * halfHorizontal + PlayerMovement.GroundSnapDistance
            || MathF.Abs(toY - middleY)
                > MaximumRisePerMetre * halfHorizontal + PlayerMovement.GroundSnapDistance)
            return false;

        float distance = MathF.Sqrt(horizontal * horizontal + (toY - fromY) * (toY - fromY));
        cost = distance * NavCosts.WalkOneMetre;
        return true;
    }

    /// <summary>Memoized <see cref="TryPosition(ChunkMap, NavCell, out Vector3)"/>.</summary>
    public static bool TryPosition(NavProbeCache cache, NavCell cell, out Vector3 position)
    {
        if (!Standable(cache, cell.X, cell.Y, cell.Z, out float surfaceY))
        {
            position = default;
            return false;
        }
        position = new Vector3(cell.X + 0.5f, surfaceY, cell.Z + 0.5f);
        return true;
    }

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
        var cursor = new VoxelCursor(map);
        return TryFindStandable(ref cursor, x, z, aroundY, below, above, out cell, out surfaceY);
    }

    /// <summary>Cursor-sharing <see cref="TryFindStandable(ChunkMap, int, int, int, int, int, out NavCell, out float)"/>.
    /// The vertical scan stays inside one column, so every probe after the first is a memo hit.</summary>
    public static bool TryFindStandable(
        ref VoxelCursor cursor,
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
                && TryAt(ref cursor, x, z, aroundY + offset, out cell, out surfaceY))
                return true;
            if (offset != 0
                && offset <= below
                && TryAt(ref cursor, x, z, aroundY - offset, out cell, out surfaceY))
                return true;
        }
        return false;

        // Static with everything passed explicitly: a local function cannot close over a ref
        // parameter, and the memo has to reach the standability check to be worth anything.
        static bool TryAt(
            ref VoxelCursor cursor, int x, int z, int y, out NavCell found, out float height)
        {
            found = default;
            height = 0f;
            if (y < ChunkConstants.WorldMinY
                || y >= ChunkConstants.WorldMaxY - 1
                || !Standable(ref cursor, x, y, z, out height))
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
    /// Resolves a destination to the physically nearest capsule-valid surface. Unlike actor-start
    /// resolution, a goal must not bind to a newly excavated floor directly below the authored
    /// point when intact grade beside the cut is closer in three dimensions.
    /// </summary>
    public static bool TryFindNearestStandableGoal(
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
        bool found = false;
        float bestDistanceSquared = float.PositiveInfinity;
        NavCell bestCell = default;
        for (int radius = 0; radius <= horizontalRadius; radius++)
        {
            // Every cell on this and later rings is at least radius - 0.5 metres away
            // horizontally. Once that lower bound cannot beat the best capsule-valid surface in
            // three dimensions, the remaining rings cannot change the answer. This normally keeps
            // actor/spawn resolution to the centre cell while still avoiding a freshly excavated
            // floor directly below an intact requested destination.
            float ringLowerBound = MathF.Max(0f, radius - 0.5f);
            if (found && ringLowerBound * ringLowerBound >= bestDistanceSquared)
                break;

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
                            out var nearby,
                            out float nearbyY))
                        Consider(nearby, nearbyY);

                    if (SurfaceQuery.HighestSurfaceY(map, x, z) is not { } surfaceY
                        || !TryFindStandable(
                            map,
                            x,
                            z,
                            (int)MathF.Floor(surfaceY),
                            below: 2,
                            above: 2,
                            out var highest,
                            out float highestY))
                        continue;
                    Consider(highest, highestY);
                }
        }

        cell = bestCell;
        return found;

        void Consider(NavCell candidate, float surfaceY)
        {
            float deltaX = candidate.X + 0.5f - position.X;
            float deltaY = surfaceY - position.Y;
            float deltaZ = candidate.Z + 0.5f - position.Z;
            float distanceSquared = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
            if (distanceSquared >= bestDistanceSquared) return;
            found = true;
            bestDistanceSquared = distanceSquared;
            bestCell = candidate;
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
        var cursor = new VoxelCursor(map);
        return TryStep(ref cursor, from, to, out cost);
    }

    /// <summary>Cursor-sharing <see cref="TryStep(ChunkMap, NavCell, NavCell, out float)"/>. The two
    /// endpoints and the midpoint are adjacent columns, so they share a chunk nearly always.</summary>
    public static bool TryStep(
        ref VoxelCursor cursor,
        NavCell from,
        NavCell to,
        out float cost)
    {
        cost = NavCosts.Inf;
        int dx = to.X - from.X;
        int dz = to.Z - from.Z;
        if ((dx == 0 && dz == 0) || Math.Abs(dx) > 1 || Math.Abs(dz) > 1)
            return false;
        if (!Standable(ref cursor, from.X, from.Y, from.Z, out float fromY)
            || !Standable(ref cursor, to.X, to.Y, to.Z, out float toY))
            return false;

        float horizontal = MathF.Sqrt(dx * dx + dz * dz);
        if (MathF.Abs(toY - fromY)
            > MaximumRisePerMetre * horizontal + PlayerMovement.GroundSnapDistance)
            return false;

        float middleX = (from.X + to.X + 1f) * 0.5f;
        float middleZ = (from.Z + to.Z + 1f) * 0.5f;
        int middleCellY = (int)MathF.Floor((fromY + toY) * 0.5f);
        if (!TryStandableAtNear(
                ref cursor,
                middleX,
                middleZ,
                middleCellY,
                MaximumTraverseCellDelta,
                out float middleY))
            return false;

        float halfHorizontal = horizontal * 0.5f;
        if (MathF.Abs(middleY - fromY)
                > MaximumRisePerMetre * halfHorizontal + PlayerMovement.GroundSnapDistance
            || MathF.Abs(toY - middleY)
                > MaximumRisePerMetre * halfHorizontal + PlayerMovement.GroundSnapDistance)
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

        // BOTH ends go through TryPosition. The destination was the only one checked, and the source
        // is the end that actually goes stale: a node is discovered standable, sits in the open set
        // while the search expands elsewhere, and a dig lands on it before the edge out of it is
        // validated. That is an ordinary event on a worker thread, and Position would answer it by
        // terminating the process.
        if (!TryPosition(map, from, out Vector3 origin)
            || !TryPosition(map, to, out Vector3 target))
            return false;
        float targetY = target.Y;

        var state = new MoveState
        {
            Position = origin,
            Velocity = Vector3.Zero,
            Grounded = true,
        };
        Vector3 intent = Vector3.Normalize(new Vector3(dx, 0f, dz));
        float arrivalRadiusSquared = 0.55f * 0.55f;
        float verticalTolerance =
            PlayerMovement.GroundSnapDistance + PlayerMovement.SkinWidth + 0.15f;

        // A stalled-progress early-out was tried here and REVERTED. It is a genuine cost — a rejected
        // edge runs the solver all fifteen ticks while an accepted one returns on arrival, and
        // rejections are the common case — but NeedsWalkValidation only fires on ASCENTS, so every
        // edge reaching this loop is a climb, and a climbing capsule legitimately makes little
        // progress for several ticks while it rises and ground-snaps over a lip. Horizontal-only
        // stall detection failed the conquest capture scenarios 4 runs of 4; measuring progress in 3D
        // and allowing six stalled ticks still failed 3 of 4, against a baseline that fails 1 of 4.
        //
        // The deeper problem is that this cannot be landed safely while the navigation suite is
        // flaky, because a change that deletes walkable edges from the graph and a bad run look
        // identical. Deterministic budgets first (docs/TODO.md), then this.
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
        if (!TryPosition(map, cell, out var position))
            throw new ArgumentException($"Navigation cell {cell} is not standable", nameof(cell));
        return position;
    }

    /// <summary>
    /// <see cref="Position"/> for callers that can be handed a cell terrain has moved out from
    /// under — anything running on a navigation worker, where the cell was sampled on the main
    /// thread at some earlier tick and a dig may have landed since.
    ///
    /// Throwing is right for the main thread, where an unstandable cell means a logic error. It is
    /// wrong on a worker: an unhandled exception there terminates the whole process, and "the world
    /// changed while I was thinking" is an ordinary event on a worker, not a bug. Those callers
    /// treat a false here as a stale request and let the agent ask again from a fresh cell.
    /// </summary>
    public static bool TryPosition(ChunkMap map, NavCell cell, out Vector3 position)
    {
        var cursor = new VoxelCursor(map);
        return TryPosition(ref cursor, cell, out position);
    }

    /// <summary>Cursor-sharing <see cref="TryPosition(ChunkMap, NavCell, out Vector3)"/>.</summary>
    public static bool TryPosition(ref VoxelCursor cursor, NavCell cell, out Vector3 position)
    {
        if (!Standable(ref cursor, cell.X, cell.Y, cell.Z, out float surfaceY))
        {
            position = default;
            return false;
        }
        position = new Vector3(cell.X + 0.5f, surfaceY, cell.Z + 0.5f);
        return true;
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
            || !TryPosition(map, from, out Vector3 origin))
            return false;

        var state = new MoveState
        {
            Position = origin,
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
            // A jump under a low ceiling never leaves the ground, and used to spend all forty ticks
            // proving it — the same asymmetry walk validation had, where the rejection cost more
            // than the acceptance.
            if (!becameAirborne && tick >= MaximumJumpLaunchTicks) return false;
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
        => TryDig(map, from, dx, dz, out target, out cost, out _);

    /// <summary>The escape-planner form also reports the tread whose headroom is being cleared, so
    /// the live actor can steer to that exact cell instead of overrunning it into the next wall.</summary>
    public static bool TryDig(
        ChunkMap map,
        NavCell from,
        int dx,
        int dz,
        out Vector3 target,
        out float cost,
        out NavCell? treadCell)
    {
        target = default;
        cost = NavCosts.Inf;
        treadCell = null;
        if (Math.Abs(dx) + Math.Abs(dz) != 1
            || !TryPosition(map, from, out Vector3 feet))
            return false;
        Vector3 direction = Vector3.Normalize(new Vector3(dx, 0f, dz));

        // A substantially higher standable surface ahead means this is the side of a pit or
        // embankment, so cut a STEP into it rather than a hole — see TryStaircaseTarget. The column
        // that HOLDS that surface is the one the stair is cut into, which is not always the cell
        // straight ahead: at the bottom of a pit the actor is often a cell short of the wall.
        int wallSteps = TryFindHigherSurface(1, out _) ? 1
                      : TryFindHigherSurface(2, out _) ? 2
                      : 0;
        if (wallSteps > 0)
        {
            // And if no step can be cut, cut NOTHING from here rather than falling through to the
            // level bite below. The two branches were digging against each other: the forward dig
            // drives straight through the tread the stair depends on, and the hole always won.
            if (!TryStaircaseTarget(
                    map,
                    from,
                    dx,
                    dz,
                    wallSteps,
                    out target,
                    out int workLayers))
                return false;
            treadCell = new NavCell(
                from.X + dx * wallSteps,
                from.Y + 1,
                from.Z + dz * wallSteps);
            // A spherical bite influences the adjacent samples on one clearance layer. Count
            // unresolved height layers rather than grid points, which avoids making the estimate
            // depend on which side of a voxel boundary an otherwise symmetric wall occupies.
            int estimatedBites = Math.Max(1, workLayers);
            cost = NavCosts.DigOneVoxel
                 + (estimatedBites - 1) * NavCosts.DigExecutionSeconds;
            return true;
        }

        // Probe the capsule axis, low to high. Removing the lowest blocker first avoids carving a
        // decorative hole above an obstruction the actor still cannot walk through. Dedicated
        // low-clearance recovery below sweeps the sphere boundary; generic frontier excavation
        // deliberately stays on-axis so it does not nibble away a staircase tread from above.
        Span<Vector3> targets = stackalloc Vector3[CapsuleBody.SampleCount];
        int targetCount = 0;
        for (int i = 0; i < CapsuleBody.SampleCount; i++)
        {
            Vector3 origin = PlayerMovement.Body.SampleCenter(feet, i);
            if (!TryDigTargetAlongRay(map, origin, direction, direction, out var sampleTarget))
                continue;
            bool duplicate = false;
            for (int j = 0; j < targetCount; j++)
                duplicate |= targets[j] == sampleTarget;
            if (duplicate) continue;
            targets[targetCount++] = sampleTarget;
        }

        if (targetCount > 0)
        {
            target = targets[0];
            cost = NavCosts.DigOneVoxel
                 + NavCosts.DigTunnelPenalty
                 + (targetCount - 1) * NavCosts.DigExecutionSeconds;
            return true;
        }

        // A continuous low roof has no vertical face for the actor's current horizontal rays once
        // the entrance bite is open. Probe the capsule at the intended result cell; this finds the
        // next overlapping lintel sample while retaining TryDigClearance's floor protection. The
        // action is accepted only when the current actor can legally reach the returned bite.
        Vector3 nextFeet = feet + direction;
        if (TryDigClearance(
                map,
                nextFeet,
                dx,
                dz,
                out target,
                maximumForward: 1)
            && Digging.InReach(feet, target))
        {
            cost = NavCosts.DigOneVoxel + NavCosts.DigTunnelPenalty;
            return true;
        }

        return false;

        bool TryFindHigherSurface(int steps, out SurfaceQuery.SurfaceHit upper)
        {
            upper = default;
            int x = from.X + dx * steps;
            int z = from.Z + dz * steps;
            var surface = SurfaceQuery.HighestSurface(
                map,
                x,
                z);
            if (surface is not { } found
                // TryDig is reached only after ordinary traversal already failed. Once a wall is
                // being converted into a tread, its sampled top drops below the old two-cell gate
                // before the capsule aperture is actually standable; switching to a forward bite
                // at that point destroys the tread and makes a horizontal tunnel. Any remaining
                // positive lip stays a staircase until TryStaircaseTarget's authoritative
                // Standable check says the step is complete.
                || found.Y <= feet.Y + 0.1f
                || !IsSoil(found.Material)
                // A sub-cell SDF lip can report a slightly higher surface even though the intended
                // tread is already air. That is an ordinary wall/tunnel frontier, not a staircase;
                // selecting it here would make TryStaircaseTarget reject the missing tread and
                // suppress the valid forward bite.
                || !IsSolidSoil(map, new Vector3(x, from.Y + 1, z)))
                return false;
            upper = found;
            return true;
        }
    }

    /// <summary>
    /// Clears a low entrance around an actor whose current position is not capsule-standable. This
    /// deliberately does not require a NavCell: resolving an unrelated surface above a cramped
    /// tunnel as the actor's cell is the bug this recovery exists to avoid.
    /// </summary>
    public static bool TryDigClearance(
        ChunkMap map,
        Vector3 feet,
        int dx,
        int dz,
        out Vector3 target,
        int maximumForward = 2)
    {
        target = default;
        if (Math.Abs(dx) + Math.Abs(dz) != 1) return false;
        Vector3 direction = Vector3.Normalize(new Vector3(dx, 0f, dz));

        // Head first. A low lintel can leave the feet path visually open while the capsule's upper
        // samples collide; clearing the lowest sample first would turn that into a crawl-height
        // tunnel the standing movement model can never use.
        for (int i = CapsuleBody.SampleCount - 1; i >= 0; i--)
        {
            Vector3 origin = PlayerMovement.Body.SampleCenter(feet, i);
            if (TryDigTargetForCapsuleSample(map, origin, direction, out target))
                return true;
        }

        // Once the actor has edged under a lintel there may be no forward surface left for a ray
        // to cross: the head sphere already overlaps the ceiling. Resolve that actual capsule
        // contact into the soil sample behind it instead of waiting forever for a NavCell that
        // cannot exist until the overlap is removed.
        // Match navigation's conservative rest/snap envelope, not only hard collision. A capsule
        // can be 0.1 m from a jagged CSG ceiling and technically non-penetrating while no stable
        // standable sample exists for the planner.
        float requiredClearance = PlayerMovement.Body.Radius
                                + PlayerMovement.SkinWidth
                                + PlayerMovement.GroundSnapDistance;
        for (int i = CapsuleBody.SampleCount - 1; i >= 0; i--)
        {
            Vector3 centre = PlayerMovement.Body.SampleCenter(feet, i);
            if (!TerrainCollision.TrySample(map, centre, out var contact)
                || contact.Distance >= requiredClearance
                // Upward-facing contact is the floor supporting the actor. Clearance recovery may
                // cut a ceiling or wall, never the tread out from under its own feet.
                || contact.Normal.Y > 0.25f)
                continue;
            Vector3 surface = centre - contact.Normal * contact.Distance;
            Vector3 candidate = Digging.TargetVoxel(surface, contact.Normal);
            if (IsSolidSoil(map, candidate))
            {
                target = candidate;
                return true;
            }
            for (int depth = 1; depth <= 2; depth++)
            {
                Vector3 deeper = candidate - contact.Normal * depth;
                deeper = new Vector3(
                    MathF.Round(deeper.X),
                    MathF.Round(deeper.Y),
                    MathF.Round(deeper.Z));
                if (!IsSolidSoil(map, deeper)) continue;
                target = deeper;
                return true;
            }
        }

        // CSG cuts can make the local gradient point diagonally away from the last remaining roof
        // sample, so the contact projection above may legitimately find only air. Sweep only the
        // 2x2 sample footprint of each one-metre route cell. The old lateral-radius sweep reached
        // three metres to either side and turned a missed lintel sample into a broad excavation.
        int baseX = (int)MathF.Floor(feet.X);
        int baseZ = (int)MathF.Floor(feet.Z);
        int firstHeadY = (int)MathF.Floor(feet.Y + PlayerMovement.Body.Height);
        for (int y = firstHeadY; y <= firstHeadY + 2; y++)
            for (int forward = 0; forward <= maximumForward; forward++)
                for (int sampleZ = 0; sampleZ <= 1; sampleZ++)
                for (int sampleX = 0; sampleX <= 1; sampleX++)
                {
                    int x = baseX + dx * forward + sampleX;
                    int z = baseZ + dz * forward + sampleZ;
                    var candidate = new Vector3(x, y, z);
                    if (!TryVoxel(map, candidate, out var voxel)
                        || voxel.Distance >= StaircaseClearedDistance
                        || voxel.Distance < 0f && !IsSoil(voxel)
                        // A positive near-surface sample can be a valid brush centre, but only if
                        // the brush actually overlaps mutable soil. Otherwise a stone lintel would
                        // masquerade as an air-labelled dig target and replan forever.
                        || !HasSoilWithinBite(map, candidate))
                        continue;
                    target = candidate;
                    return true;
                }
        return false;
    }

    private static bool HasSoilWithinBite(ChunkMap map, Vector3 centre)
    {
        foreach (var offset in (ReadOnlySpan<Vector3>)[
                     Vector3.Zero,
                     Vector3.UnitX, -Vector3.UnitX,
                     Vector3.UnitY, -Vector3.UnitY,
                     Vector3.UnitZ, -Vector3.UnitZ])
            if (TryVoxel(map, centre + offset, out var voxel)
                && voxel.Distance < 0f
                && IsSoil(voxel))
                return true;
        return false;
    }

    private static bool TryDigTargetForCapsuleSample(
        ChunkMap map,
        Vector3 centre,
        Vector3 direction,
        out Vector3 target)
    {
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        float radius = PlayerMovement.Body.Radius;
        foreach (var offset in (ReadOnlySpan<Vector3>)[
                     Vector3.UnitY * radius,
                     right * radius,
                     -right * radius,
                     Vector3.Zero])
        {
            Vector3 inward = offset == Vector3.Zero
                ? direction
                : Vector3.Normalize(direction + offset / radius);
            if (TryDigTargetAlongRay(
                    map,
                    centre + offset,
                    direction,
                    inward,
                    out target))
                return true;
        }

        target = default;
        return false;
    }

    /// <summary>
    /// Samples of headroom cleared above each tread. More than the 1.8 m capsule needs, and the
    /// asymmetry is measured rather than assumed — escaping the DigEscapeTests pit costs:
    ///
    ///     2 cells -> 640 bites      3 cells -> 158 bites      4 cells -> 166 bites
    ///
    /// Under-cutting is not slightly worse, it is four times worse: a tread that only just clears
    /// the capsule keeps failing the standability check, so the actor re-cuts the same step instead
    /// of climbing it. Over-cutting only wastes the bites it spends. When in doubt, cut more.
    /// </summary>
    private const int StaircaseHeadroomCells = 4;

    /// <summary>
    /// How positive a sample has to read before the headroom counts as cut. Not simply "air":
    /// ClicksPerVoxel makes one bite a HALF bite, so a sample can cross zero — and relabel itself
    /// Air — while the surface has barely moved and the capsule still will not fit.
    /// </summary>
    private const float StaircaseClearedDistance = 0.5f;

    /// <summary>
    /// The next bite of a staircase cut into a wall the actor cannot climb.
    ///
    /// Digging a step is the OPPOSITE of digging a hole: the tread is the material you LEAVE, so the
    /// bite has to take the headroom above it. That is why aiming a ray upward never worked — the
    /// brush is a sphere centred on whatever it hits, and a sphere removes as much below the aim
    /// point as above it, so pointing it further up a vertical wall only ever produced a higher
    /// alcove. A dent with no floor is not a step however many bites go into it.
    ///
    /// One cell of rise per step, cut bottom-up: returning the LOWEST sample still in the way means
    /// successive replans clear the column in order and the tread turns standable as soon as the
    /// capsule fits, rather than opening a window above an obstruction that is still there.
    /// </summary>
    private static bool TryStaircaseTarget(
        ChunkMap map,
        NavCell from,
        int dx,
        int dz,
        int wallSteps,
        out Vector3 target,
        out int workSamples)
    {
        target = default;
        workSamples = 0;
        int workLayers = 0;

        int x = from.X + dx * wallSteps;
        int z = from.Z + dz * wallSteps;

        // MaximumTraverseCellDelta allows two, but a stair the actor can only just manage is one
        // the movement solver gets to veto. A single cell always climbs.
        int tread = from.Y + 1;

        // The tread has to be something to stand on. Open air here is not a wall to step up, and
        // the forward dig is the right tool for that.
        if (!IsSolidSoil(map, new Vector3(x, tread, z))) return false;

        // A CELL is bounded by two samples on each horizontal axis, so clearing a single sample
        // column leaves solid material half a metre from where the capsule would stand and the step
        // is never standable — which is exactly what a one-column cut produced: a beautifully shaped
        // pocket nobody could get into. Clear the cell's whole 2x2 footprint, bottom-up, so the
        // space the capsule actually occupies opens level by level.
        for (int y = tread + 1; y <= tread + StaircaseHeadroomCells; y++)
            for (int stepZ = 0; stepZ <= 1; stepZ++)
                for (int stepX = 0; stepX <= 1; stepX++)
                {
                    var sample = new Vector3(x + stepX, y, z + stepZ);
                    if (!TryVoxel(map, sample, out var voxel)) return false;
                    if (voxel.Distance >= StaircaseClearedDistance) continue;

                    // Rock in one corner is not a reason to abandon the staircase, it is a reason to
                    // cut the rest of it. SubtractSoil would refuse this sample anyway.
                    if (voxel.Distance < 0f && !IsSoil(voxel)) continue;

                    if (workLayers == 0) target = sample;
                    workLayers |= 1 << (y - tread - 1);
                }

        if (workLayers != 0)
        {
            workSamples = BitOperations.PopCount((uint)workLayers);
            return true;
        }

        var treadCell = new NavCell(x, tread, z);
        if (Standable(map, treadCell.X, treadCell.Y, treadCell.Z, out _))
            return false;

        // If interpolation still leaves a rounded shoulder against the capsule, remove the actual
        // contact rather than sweeping a 4x4 apron. This keeps every staircase one route cell wide:
        // extra work follows the body's boundary instead of opening a room around it.
        return TryStaircaseShoulderTarget(map, treadCell, out target, out workSamples);
    }

    private static bool TryStaircaseShoulderTarget(
        ChunkMap map,
        NavCell treadCell,
        out Vector3 target,
        out int workSamples)
    {
        target = default;
        workSamples = 0;
        var centre = new Vector3(treadCell.X + 0.5f, treadCell.Y, treadCell.Z + 0.5f);
        if (!TerrainCollision.TrySampleRaw(map, centre, out float below)
            || !TerrainCollision.TrySampleRaw(map, centre + Vector3.UnitY, out float above)
            || below >= -SurfaceEpsilon
            || above < 0f)
            return false;

        float denominator = above - below;
        if (denominator <= SurfaceEpsilon) return false;
        float surfaceY = treadCell.Y + Math.Clamp(-below / denominator, 0f, 1f);
        Vector3 feet = centre with { Y = surfaceY };
        float requiredClearance = PlayerMovement.Body.Radius
                                + PlayerMovement.SkinWidth
                                + PlayerMovement.GroundSnapDistance;

        for (int i = CapsuleBody.SampleCount - 1; i >= 0; i--)
        {
            Vector3 sampleCentre = PlayerMovement.Body.SampleCenter(feet, i);
            if (!TerrainCollision.TrySample(map, sampleCentre, out var contact)
                || contact.Distance >= requiredClearance
                || contact.Normal.Y > 0.25f)
                continue;

            Vector3 surface = sampleCentre - contact.Normal * contact.Distance;
            Vector3 candidate = Digging.TargetVoxel(surface, contact.Normal);
            for (int depth = 0; depth <= 2; depth++)
            {
                Vector3 deeper = candidate - contact.Normal * depth;
                deeper = new Vector3(
                    MathF.Round(deeper.X),
                    MathF.Round(deeper.Y),
                    MathF.Round(deeper.Z));
                if (deeper.Y <= treadCell.Y || !IsSolidSoil(map, deeper)) continue;
                target = deeper;
                workSamples = 1;
                return true;
            }
        }

        return false;
    }

    private static bool TryDigTargetAlongRay(
        ChunkMap map,
        Vector3 origin,
        Vector3 rayDirection,
        Vector3 inwardDirection,
        out Vector3 target)
    {
        target = default;
        // The navigation cell is at the actor centre while collision stops the capsule radius plus
        // skin before a low entrance. A 1.75 m ray could therefore end just short of a legally
        // reachable lintel and leave both search and execution waiting forever at its mouth.
        if (TerrainRaycast.Cast(map, origin, rayDirection, 2.5f) is not { } hit)
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
        ref VoxelCursor cursor,
        float x,
        float z,
        int aroundY,
        int range,
        out float surfaceY)
    {
        surfaceY = 0f;
        for (int offset = 0; offset <= range; offset++)
        {
            if (StandableAt(ref cursor, x, aroundY + offset, z, out surfaceY))
                return true;
            if (offset != 0 && StandableAt(ref cursor, x, aroundY - offset, z, out surfaceY))
                return true;
        }
        return false;
    }

    private static bool StandableAt(
        ref VoxelCursor cursor,
        float x,
        int y,
        float z,
        out float surfaceY)
    {
        surfaceY = 0f;
        if (y < ChunkConstants.WorldMinY || y >= ChunkConstants.WorldMaxY - 1)
            return false;

        if (!TerrainCollision.TrySampleRaw(ref cursor, new Vector3(x, y, z), out float below)
            || !TerrainCollision.TrySampleRaw(ref cursor, new Vector3(x, y + 1f, z), out float above)
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
                    ref cursor,
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
