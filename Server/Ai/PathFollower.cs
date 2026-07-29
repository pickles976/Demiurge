using System.Numerics;

namespace Demiurge.GameServer;

internal enum PathFollowState
{
    NeedsPath,
    Following,
    Complete,
}

/// <summary>Turns navigation waypoints into the same normalized intent used by player input.</summary>
internal sealed class PathFollower
{
    private const float ArrivalRadius = 0.55f;
    private const float ProgressEpsilon = 0.025f;
    private const int StallTicks = NetworkConfig.TickRate;

    private NavPath? path;
    private int waypoint;
    private int stalledTicks;
    private float bestDistance = float.PositiveInfinity;
    private long terrainVersion;
    private bool jumpIssued;

    public bool ReachedGoal => path?.ReachedGoal == true;

    public void SetPath(NavPath value, long version)
    {
        path = value;
        terrainVersion = version;
        waypoint = 0;
        stalledTicks = 0;
        bestDistance = float.PositiveInfinity;
        jumpIssued = false;
    }

    public void Clear()
    {
        path = null;
        waypoint = 0;
        stalledTicks = 0;
        bestDistance = float.PositiveInfinity;
        jumpIssued = false;
    }

    public PathFollowState Update(
        Vector3 position,
        bool grounded,
        long currentTerrainVersion,
        out Vector3 intent,
        out bool jump)
    {
        intent = Vector3.Zero;
        jump = false;
        if (path is null || terrainVersion != currentTerrainVersion)
        {
            Clear();
            return PathFollowState.NeedsPath;
        }

        while (waypoint < path.Waypoints.Count
               && HorizontalDistanceSquared(position, path.Waypoints[waypoint].Position)
                   <= ArrivalRadius * ArrivalRadius)
        {
            waypoint++;
            stalledTicks = 0;
            bestDistance = float.PositiveInfinity;
            jumpIssued = false;
        }

        if (waypoint >= path.Waypoints.Count)
            return PathFollowState.Complete;

        Vector3 delta = path.Waypoints[waypoint].Position - position;
        delta.Y = 0f;
        float distance = delta.Length();
        if (distance <= 1e-6f)
            return PathFollowState.Following;

        if (distance < bestDistance - ProgressEpsilon)
        {
            bestDistance = distance;
            stalledTicks = 0;
        }
        else if (++stalledTicks >= StallTicks)
        {
            Clear();
            return PathFollowState.NeedsPath;
        }

        intent = delta / distance;
        if (path.Waypoints[waypoint].Action == NavAction.Jump
            && grounded
            && !jumpIssued)
        {
            jump = true;
            jumpIssued = true;
        }
        return PathFollowState.Following;
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
