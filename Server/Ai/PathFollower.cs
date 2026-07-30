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
    private const float ProgressEpsilon = 0.025f;
    private const float UphillRecoveryRise = 0.2f;
    private const int UphillRecoveryTicks = NetworkConfig.TickRate / 4;
    private const int MaxUphillRecoveryAttempts = 2;
    private const int StallTicks = 3 * NetworkConfig.TickRate / 4;
    private const float PartialRefreshDistance = 12f;

    private NavPath? path;
    private int waypoint;
    private int stalledTicks;
    private int uphillRecoveryAttempts;
    private float bestDistance = float.PositiveInfinity;
    private long terrainVersion;
    private ChunkMap? terrain;
    private bool jumpIssued;
    private bool jumpBecameAirborne;
    private Vector3 jumpIntent;
    private Vector3 lastPosition;
    private bool hasLastPosition;

    public bool ReachedGoal => path?.ReachedGoal == true;
    public bool ShouldRefreshPath =>
        path is { ReachedGoal: false }
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
        jumpIntent = Vector3.Zero;
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
        for (int i = 0; i < value.Waypoints.Count; i++)
        {
            // Never skip a planned jump or dig just because an asynchronously moving actor is
            // horizontally close to its destination when the replacement path arrives.
            if (value.Waypoints[i].Action != NavAction.Walk)
                break;

            float distance = HorizontalDistanceSquared(position, value.Waypoints[i].Position);
            if (distance >= closestDistance) continue;
            closest = i;
            closestDistance = distance;
        }

        // The closest ordinary waypoint is normally the stale request origin or a point the actor
        // has just passed. Joining at its successor preserves forward motion.
        return Math.Min(closest + 1, value.Waypoints.Count);
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
        jumpIntent = Vector3.Zero;
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
        if (path is null
            || (terrain is not null
                ? !NavPathTerrain.IsValid(terrain, path)
                : terrainVersion != currentTerrainVersion))
        {
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
                intent = jumpIntent;
                jump = true;
                return PathFollowState.Following;
            }
            if (!grounded)
            {
                intent = jumpIntent;
                return PathFollowState.Following;
            }

            waypoint++;
            stalledTicks = 0;
            uphillRecoveryAttempts = 0;
            bestDistance = float.PositiveInfinity;
            jumpIssued = false;
            jumpBecameAirborne = false;
            jumpIntent = Vector3.Zero;
        }

        while (waypoint < path.Waypoints.Count
               && path.Waypoints[waypoint].Action != NavAction.Dig
               && HorizontalDistanceSquared(position, path.Waypoints[waypoint].Position)
                   <= ArrivalRadius * ArrivalRadius)
        {
            waypoint++;
            stalledTicks = 0;
            uphillRecoveryAttempts = 0;
            bestDistance = float.PositiveInfinity;
            jumpIssued = false;
            jumpBecameAirborne = false;
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
            jumpIntent = intent;
        }
        else if (path.Waypoints[waypoint].Action == NavAction.Walk
                 && grounded
                 && stalledTicks >= UphillRecoveryTicks
                 && uphillRecoveryAttempts < MaxUphillRecoveryAttempts
                 && path.Waypoints[waypoint].Position.Y - position.Y >= UphillRecoveryRise)
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
