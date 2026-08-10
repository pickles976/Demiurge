using System.Numerics;

namespace Demiurge;

public enum NavAction : byte
{
    Walk,
    Jump,
    /// <summary>
    /// Clear the soil at this waypoint before planning onward. Unlike walk and jump, Position is
    /// the voxel targeted by the shovel rather than a place the actor can already stand.
    /// </summary>
    Dig,
}

/// <param name="Action">Action used to travel from the previous waypoint to this one.</param>
public readonly record struct NavWaypoint(
    NavCell Cell,
    Vector3 Position,
    NavAction Action = NavAction.Walk);

public readonly record struct NavChunkRevision(
    ChunkIndex Chunk,
    long Revision);

/// <param name="ExhaustedReachable">
/// The search emptied its open set rather than stopping on a node or time budget, so every cell
/// ordinary movement can reach from the start was examined. Only meaningful when the goal was not
/// reached, where it distinguishes "cannot get there" from "did not have time to".
/// </param>
public sealed record NavPath(
    IReadOnlyList<NavWaypoint> Waypoints,
    bool ReachedGoal,
    float Cost,
    int ExpandedNodes,
    int CacheHits = 0,
    IReadOnlyList<NavChunkRevision>? CorridorRevisions = null,
    bool ExhaustedReachable = false)
{
    public static NavPath Failed(int expandedNodes = 0)
        => new([], false, NavCosts.Inf, expandedNodes);
}

/// <summary>Captures and validates the terrain chunks intersecting a path plus a safety apron.</summary>
public static class NavPathTerrain
{
    public const int ChunkApron = 1;

    public static bool TryStamp(
        ChunkMap map,
        NavPath path,
        long searchStartedVersion,
        out NavPath stamped)
    {
        var chunks = CorridorChunks(path);
        var revisions = new NavChunkRevision[chunks.Count];
        int i = 0;
        foreach (var chunk in chunks.OrderBy(chunk => chunk.x).ThenBy(chunk => chunk.z))
        {
            long revision = map.ChunkEditVersion(chunk);
            if (revision > searchStartedVersion)
            {
                stamped = NavPath.Failed(path.ExpandedNodes);
                return false;
            }
            revisions[i++] = new NavChunkRevision(chunk, revision);
        }

        stamped = path with { CorridorRevisions = revisions };
        return true;
    }

    public static bool IsValid(ChunkMap map, NavPath path)
    {
        if (path.CorridorRevisions is not { } revisions)
            return false;
        foreach (var entry in revisions)
            if (map.ChunkEditVersion(entry.Chunk) != entry.Revision)
                return false;
        return true;
    }

    private static HashSet<ChunkIndex> CorridorChunks(NavPath path)
    {
        var chunks = new HashSet<ChunkIndex>();
        for (int waypointIndex = 0; waypointIndex < path.Waypoints.Count; waypointIndex++)
        {
            var waypoint = path.Waypoints[waypointIndex];
            AddWithApron(waypoint.Cell.X, waypoint.Cell.Z);
            if (waypointIndex == 0) continue;

            // Smoothing can collapse a long straight run to only its endpoints. Sample each segment
            // at half-chunk intervals so those omitted middle chunks remain part of the revision
            // corridor. The one-chunk apron covers corner crossings and the actor capsule.
            var previous = path.Waypoints[waypointIndex - 1].Cell;
            int dx = waypoint.Cell.X - previous.X;
            int dz = waypoint.Cell.Z - previous.Z;
            int steps = (int)MathF.Ceiling(
                Math.Max(Math.Abs(dx), Math.Abs(dz))
                / (ChunkConstants.ChunkWidth * 0.5f));
            for (int step = 1; step < steps; step++)
            {
                float t = step / (float)steps;
                AddWithApron(
                    (int)MathF.Floor(previous.X + dx * t),
                    (int)MathF.Floor(previous.Z + dz * t));
            }
        }
        return chunks;

        void AddWithApron(int worldX, int worldZ)
        {
            var centre = ChunkTransforms.ChunkAt(worldX, worldZ);
            for (int z = centre.z - ChunkApron; z <= centre.z + ChunkApron; z++)
                for (int x = centre.x - ChunkApron; x <= centre.x + ChunkApron; x++)
                    chunks.Add(new ChunkIndex { x = x, z = z });
        }
    }
}

/// <summary>Removes redundant steering points without crossing a non-walk traversal action.</summary>
public static class NavPathSmoothing
{
    // Four-metre steering anchors keep small lateral errors from accumulating along a long narrow
    // bridge. Eight-metre segments were fine in ideal path tests but gave the live follower enough
    // time to drift to an edge before receiving another centre-line correction.
    public const float MaximumWalkSegmentLength = 4f;

    public static NavPath RemoveCollinearWalks(NavPath path)
    {
        if (path.Waypoints.Count < 3) return path;

        var smoothed = new List<NavWaypoint>(path.Waypoints.Count)
        {
            path.Waypoints[0],
        };
        for (int i = 1; i < path.Waypoints.Count - 1; i++)
        {
            var previous = smoothed[^1];
            var current = path.Waypoints[i];
            var next = path.Waypoints[i + 1];
            int firstX = current.Cell.X - previous.Cell.X;
            int firstZ = current.Cell.Z - previous.Cell.Z;
            int secondX = next.Cell.X - current.Cell.X;
            int secondZ = next.Cell.Z - current.Cell.Z;
            bool sameDirection =
                firstX * secondZ - firstZ * secondX == 0
                && firstX * secondX + firstZ * secondZ > 0;
            int retainedToNextX = next.Cell.X - previous.Cell.X;
            int retainedToNextZ = next.Cell.Z - previous.Cell.Z;
            bool withinMaximumSegment =
                retainedToNextX * retainedToNextX + retainedToNextZ * retainedToNextZ
                <= MaximumWalkSegmentLength * MaximumWalkSegmentLength;
            if (current.Action == NavAction.Walk
                && next.Action == NavAction.Walk
                && sameDirection
                && withinMaximumSegment)
                continue;
            smoothed.Add(current);
        }
        smoothed.Add(path.Waypoints[^1]);
        return path with { Waypoints = smoothed };
    }
}
