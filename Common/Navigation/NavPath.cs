using System.Numerics;

namespace Demiurge;

public enum NavAction : byte
{
    Walk,
    Jump,
}

/// <param name="Action">Action used to travel from the previous waypoint to this one.</param>
public readonly record struct NavWaypoint(
    NavCell Cell,
    Vector3 Position,
    NavAction Action = NavAction.Walk);

public sealed record NavPath(
    IReadOnlyList<NavWaypoint> Waypoints,
    bool ReachedGoal,
    float Cost,
    int ExpandedNodes)
{
    public static NavPath Failed(int expandedNodes = 0)
        => new([], false, NavCosts.Inf, expandedNodes);
}
