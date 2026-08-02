using System.Numerics;

namespace Demiurge.GameServer;

internal enum PathFollowState
{
    NeedsPath,
    Following,
    Digging,
    Complete,
}

/// <summary>Turns navigation waypoints into the same normalized intent used by player input.</summary>
internal sealed class PathFollower
{
    private const float ArrivalRadius = 0.55f;
    private const float JumpTakeoffArrivalRadius = 0.15f;
    private const float ProgressEpsilon = 0.025f;
    private const float UphillRecoveryRise = 0.2f;
    private const int UphillRecoveryTicks = NetworkConfig.TickRate / 4;
    private const int MaxUphillRecoveryAttempts = 1;
    private const int JumpTakeoffTimeoutTicks = NetworkConfig.TickRate / 2;
    private const float JumpLandingTolerance = 1.5f;
    private const int StallTicks = 3 * NetworkConfig.TickRate / 4;
    private const float PartialRefreshDistance = 12f;
    private const float ForwardJoinSlack = 1f;

    private NavPath? path;
    private int waypoint;
    private int stalledTicks;
    private int uphillRecoveryAttempts;
    private float bestDistance = float.PositiveInfinity;
    private long terrainVersion;
    private ChunkMap? terrain;
    private bool jumpIssued;
    private bool jumpBecameAirborne;
    private int jumpTakeoffTicks;
    private Vector3 jumpIntent;
    private bool staleDigIssued;
    private bool pathHasDig;
    private Vector3 lastPosition;
    private bool hasLastPosition;

    public bool ReachedGoal => path?.ReachedGoal == true;
    public bool HasPath => path is not null;
    public bool CanReplacePath => !jumpIssued;
    public bool ShouldRefreshPath =>
        path is { ReachedGoal: false }
        // A dig is already the receding-horizon decision. Prefetching the same terrain generation
        // repeatedly resets stall recovery on its walk prefix and can prevent the actor from ever
        // reaching or revising that excavation frontier.
        && !pathHasDig
        // A planned jump is one atomic movement. Replacing its landing corridor in mid-air drops
        // the executor's proven intent and can steer the actor off a one-metre bridge.
        && !jumpIssued
        && RemainingPathMetres() <= PartialRefreshDistance;

    public void SetPath(
        NavPath value,
        long version,
        Vector3? currentPosition = null,
        ChunkMap? terrain = null)
    {
        path = value;
        terrainVersion = version;
        this.terrain = terrain;
        waypoint = currentPosition is { } position
            ? ForwardJoinWaypoint(value, position)
            : 0;
        stalledTicks = 0;
        uphillRecoveryAttempts = 0;
        bestDistance = float.PositiveInfinity;
        jumpIssued = false;
        jumpBecameAirborne = false;
        jumpTakeoffTicks = 0;
        jumpIntent = Vector3.Zero;
        staleDigIssued = false;
        pathHasDig = value.Waypoints.Any(waypoint => waypoint.Action == NavAction.Dig);
        if (currentPosition is { } joinedPosition)
        {
            lastPosition = joinedPosition;
            hasLastPosition = true;
        }
        else
        {
            lastPosition = default;
            hasLastPosition = false;
        }
    }

    private static int ForwardJoinWaypoint(NavPath value, Vector3 position)
    {
        int closest = 0;
        float closestDistance = float.PositiveInfinity;
        float actorDisplacement = MathF.Sqrt(HorizontalDistanceSquared(
            position,
            value.Waypoints[0].Position));
        float maximumRouteMetres = actorDisplacement + ForwardJoinSlack;
        float routeMetres = 0f;
        for (int i = 0; i < value.Waypoints.Count; i++)
        {
            if (i > 0)
            {
                routeMetres += MathF.Sqrt(HorizontalDistanceSquared(
                    value.Waypoints[i - 1].Position,
                    value.Waypoints[i].Position));
                if (routeMetres > maximumRouteMetres)
                    break;
            }
            // Never skip a planned jump or dig just because an asynchronously moving actor is
            // horizontally close to its destination when the replacement path arrives.
            if (value.Waypoints[i].Action != NavAction.Walk)
                break;

            float distance = HorizontalDistanceSquared(position, value.Waypoints[i].Position);
            if (distance >= closestDistance) continue;
            closest = i;
            closestDistance = distance;
        }

        // Do not assume the closest point has already been passed. On a replacement route the next
        // centre-line anchor may be a bridge entrance or the foot of a staircase; skipping it while
        // still outside the normal arrival radius makes the follower cut the corner into open air
        // or push diagonally into the riser. Update consumes it immediately when the actor really is
        // close enough, so retaining the point does not introduce a pause.
        return closest;
    }

    public void Clear()
    {
        path = null;
        terrain = null;
        waypoint = 0;
        stalledTicks = 0;
        uphillRecoveryAttempts = 0;
        bestDistance = float.PositiveInfinity;
        jumpIssued = false;
        jumpBecameAirborne = false;
        jumpTakeoffTicks = 0;
        jumpIntent = Vector3.Zero;
        staleDigIssued = false;
        pathHasDig = false;
        lastPosition = default;
        hasLastPosition = false;
    }

    public PathFollowState Update(
        Vector3 position,
        bool grounded,
        long currentTerrainVersion,
        out Vector3 intent,
        out bool jump,
        out Vector3 digTarget,
        out NavCell? blockedCell)
    {
        intent = Vector3.Zero;
        jump = false;
        digTarget = Vector3.Zero;
        blockedCell = null;
        lastPosition = position;
        hasLastPosition = true;
        if (path is null)
        {
            Clear();
            return PathFollowState.NeedsPath;
        }

        bool terrainChanged = terrain is not null
            ? !NavPathTerrain.IsValid(terrain, path)
            : terrainVersion != currentTerrainVersion;
        if (terrainChanged)
        {
            // A squad often receives several paths to the same excavation frontier in one tick.
            // The first shovel bite changes the chunk revision; throwing every later path away
            // before its owner can swing makes one actor do all of the work. Let each already-
            // planned dig contribute one bite, then force a fresh search on its next update.
            int action = NextActionableWaypoint(position);
            if (!staleDigIssued
                && action < path.Waypoints.Count
                && path.Waypoints[action].Action == NavAction.Dig)
            {
                waypoint = action;
                staleDigIssued = true;
                digTarget = path.Waypoints[action].Position;
                return PathFollowState.Digging;
            }

            Clear();
            return PathFollowState.NeedsPath;
        }

        // A jump edge is planned by simulating continuous directional input until landing. Do not
        // advance the landing waypoint merely because the airborne capsule passed over its X/Z;
        // doing so removed horizontal velocity near the apex and made NPCs drop into the obstacle.
        if (jumpIssued)
        {
            jumpBecameAirborne |= !grounded;
            if (!jumpBecameAirborne)
            {
                jumpTakeoffTicks++;
                if (jumpTakeoffTicks >= JumpTakeoffTimeoutTicks)
                {
                    blockedCell = path.Waypoints[waypoint].Cell;
                    Clear();
                    return PathFollowState.NeedsPath;
                }
                intent = jumpIntent;
                jump = true;
                return PathFollowState.Following;
            }
            if (!grounded)
            {
                intent = jumpIntent;
                return PathFollowState.Following;
            }

            if (HorizontalDistanceSquared(position, path.Waypoints[waypoint].Position)
                > JumpLandingTolerance * JumpLandingTolerance)
            {
                blockedCell = path.Waypoints[waypoint].Cell;
                Clear();
                return PathFollowState.NeedsPath;
            }

            waypoint++;
            stalledTicks = 0;
            uphillRecoveryAttempts = 0;
            bestDistance = float.PositiveInfinity;
            jumpIssued = false;
            jumpBecameAirborne = false;
            jumpTakeoffTicks = 0;
            jumpIntent = Vector3.Zero;
        }

        while (waypoint < path.Waypoints.Count
               && path.Waypoints[waypoint].Action != NavAction.Dig
               && HorizontalDistanceSquared(position, path.Waypoints[waypoint].Position)
                   <= ArrivalRadiusSquaredFor(waypoint))
        {
            waypoint++;
            stalledTicks = 0;
            uphillRecoveryAttempts = 0;
            bestDistance = float.PositiveInfinity;
            jumpIssued = false;
            jumpBecameAirborne = false;
            jumpTakeoffTicks = 0;
            jumpIntent = Vector3.Zero;
        }

        if (waypoint >= path.Waypoints.Count)
            return PathFollowState.Complete;

        if (path.Waypoints[waypoint].Action == NavAction.Dig)
        {
            digTarget = path.Waypoints[waypoint].Position;
            return PathFollowState.Digging;
        }

        Vector3 delta = path.Waypoints[waypoint].Position - position;
        delta.Y = 0f;
        float distance = delta.Length();
        if (distance <= 1e-6f)
            return PathFollowState.Following;

        if (distance < bestDistance - ProgressEpsilon)
        {
            bestDistance = distance;
            stalledTicks = 0;
            uphillRecoveryAttempts = 0;
        }
        else
        {
            stalledTicks++;
        }

        intent = delta / distance;
        if (path.Waypoints[waypoint].Action == NavAction.Jump
            && grounded
            && !jumpIssued)
        {
            jump = true;
            jumpIssued = true;
            jumpTakeoffTicks = 0;
            jumpIntent = intent;
        }
        else if (path.Waypoints[waypoint].Action == NavAction.Walk
                 && grounded
                 && stalledTicks >= UphillRecoveryTicks
                 && uphillRecoveryAttempts < MaxUphillRecoveryAttempts
                 && path.Waypoints[waypoint].Position.Y - position.Y is var rise
                 && rise >= UphillRecoveryRise)
        {
            // A sampled bridge lip can occasionally pass the navigation walk validation while the
            // authoritative capsule catches on its edge. Recover locally before throwing away an
            // otherwise useful path. This is a one-tick jump pulse rather than a planned jump edge:
            // the ordinary waypoint must remain current until the actor actually walks onto it.
            jump = true;
            stalledTicks = 0;
            uphillRecoveryAttempts++;
            bestDistance = distance;
        }
        else if (stalledTicks >= StallTicks)
        {
            blockedCell = path.Waypoints[waypoint].Cell;
            Clear();
            return PathFollowState.NeedsPath;
        }
        return PathFollowState.Following;
    }

    private float ArrivalRadiusSquaredFor(int index)
    {
        float radius = index + 1 < path!.Waypoints.Count
                       && path.Waypoints[index + 1].Action == NavAction.Jump
            ? JumpTakeoffArrivalRadius
            : ArrivalRadius;
        return radius * radius;
    }

    private int NextActionableWaypoint(Vector3 position)
    {
        if (path is null) return 0;
        int action = waypoint;
        while (action < path.Waypoints.Count
               && path.Waypoints[action].Action != NavAction.Dig
               && HorizontalDistanceSquared(position, path.Waypoints[action].Position)
                   <= ArrivalRadius * ArrivalRadius)
            action++;
        return action;
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private float RemainingPathMetres()
    {
        if (path is null || waypoint >= path.Waypoints.Count)
            return 0f;
        float distance = 0f;
        Vector3 previous = hasLastPosition
            ? lastPosition
            : path.Waypoints[Math.Max(0, waypoint - 1)].Position;
        for (int i = waypoint; i < path.Waypoints.Count; i++)
        {
            Vector3 next = path.Waypoints[i].Position;
            float dx = next.X - previous.X;
            float dz = next.Z - previous.Z;
            distance += MathF.Sqrt(dx * dx + dz * dz);
            previous = next;
        }
        return distance;
    }
}
