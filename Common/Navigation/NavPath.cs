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
    /// <summary>
    /// Step off a ledge and let gravity finish the move. No input the follower has to issue — the
    /// difference from a walk is that the drop was PRICED rather than rejected, and that smoothing
    /// must not fold the ledge away.
    /// </summary>
    Fall,
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

/// <summary>
/// Captures and validates the terrain chunks intersecting a path plus a safety apron.
///
/// Validation is by PREFIX, not by whole corridor, and that distinction is the whole point of this
/// class. A corridor spans every chunk the route passes through plus a one-chunk apron; on the
/// conquest map a long objective route covers dozens of them, and sixteen NPCs digging concurrently
/// mean an edit lands in one of those chunks during most searches. Rejecting the result outright
/// measured 410-756 discarded searches per match against 1,500-2,000 issued — a quarter to a third of
/// all navigation work thrown away because somebody dug two hundred metres away.
///
/// It is also self-feeding: an actor left with no path digs, and its bites invalidate the searches of
/// everyone around it. Keeping the executable prefix breaks that loop. The follower only consumes the
/// first stretch before replanning anyway, so a route trimmed at the first dirty chunk is worth very
/// nearly as much as the whole one, and is worth infinitely more than nothing.
/// </summary>
public static class NavPathTerrain
{
    public const int ChunkApron = 1;

    /// <summary>
    /// Stamps the longest leading run of <paramref name="path"/> whose chunks are unchanged since
    /// <paramref name="searchStartedVersion"/>. False when not even the first segment survives.
    /// </summary>
    public static bool TryStamp(
        ChunkMap map,
        NavPath path,
        long searchStartedVersion,
        out NavPath stamped)
        => TryStampPrefix(
            map,
            path,
            chunk => map.ChunkEditVersion(chunk) <= searchStartedVersion,
            out stamped);

    public static bool IsValid(ChunkMap map, NavPath path)
    {
        if (path.CorridorRevisions is not { } revisions)
            return false;
        foreach (var entry in revisions)
            if (map.ChunkEditVersion(entry.Chunk) != entry.Revision)
                return false;
        return true;
    }

    /// <summary>
    /// The still-executable prefix of an already-stamped path. Use where the alternative is discarding
    /// the route: an edit behind the actor, or far ahead of it, does not stop it walking the part in
    /// between.
    /// </summary>
    public static bool TryTrimToValid(ChunkMap map, NavPath path, out NavPath trimmed)
    {
        if (path.CorridorRevisions is not { } revisions)
        {
            trimmed = NavPath.Failed(path.ExpandedNodes);
            return false;
        }

        var stamped = new Dictionary<ChunkIndex, long>(revisions.Count);
        foreach (var entry in revisions)
            stamped[entry.Chunk] = entry.Revision;
        // A chunk outside the recorded corridor cannot be judged, so it is treated as current: the
        // stamp is the authority on what this route depends on.
        return TryStampPrefix(
            map,
            path,
            chunk => !stamped.TryGetValue(chunk, out long revision)
                     || map.ChunkEditVersion(chunk) == revision,
            out trimmed);
    }

    private static bool TryStampPrefix(
        ChunkMap map,
        NavPath path,
        Func<ChunkIndex, bool> chunkIsCurrent,
        out NavPath stamped)
    {
        var corridor = new HashSet<ChunkIndex>();
        var segment = new HashSet<ChunkIndex>();
        int kept = 0;
        // The actor's own neighbourhood is exempt, and it is the dominant case rather than a corner:
        // measured on conquest, 68-80% of discarded searches were killed by an edit in the START
        // chunk — the requester's own shovel, or a squadmate's on the same leased staircase, landing
        // while the search ran. Failing there is both useless and self-feeding, because it is
        // precisely the excavating actor whose route gets deleted every bite. Seeding the corridor
        // with the first segment exempts those chunks for the whole route; everything beyond them is
        // still checked, and the follower re-derives its footing from live terrain each tick anyway.
        if (path.Waypoints.Count > 0)
        {
            AddSegmentChunks(path, 0, corridor);
            kept = 1;
        }
        for (int waypointIndex = 1; waypointIndex < path.Waypoints.Count; waypointIndex++)
        {
            segment.Clear();
            AddSegmentChunks(path, waypointIndex, segment);
            bool current = true;
            foreach (var chunk in segment)
                if (!corridor.Contains(chunk) && !chunkIsCurrent(chunk))
                {
                    current = false;
                    break;
                }
            if (!current) break;
            corridor.UnionWith(segment);
            kept = waypointIndex + 1;
        }

        // Deliberately NOT backing off a trailing Dig: an excavation macro ENDS on the voxel the
        // shovel is aimed at, so trimming to the last standable waypoint rejects every dig route.
        // Requiring two waypoints does the same thing to the shortest of them. Both were tried on
        // 2026-08-12 and together they stopped excavation dead — terrain edits fell from ~880 to
        // ~270 not because digging got tidier but because dig paths were being thrown away, and the
        // actors then re-requested about five times as often. Accept whatever the old whole-corridor
        // check would have accepted; this class decides FRESHNESS, not whether a route is worth
        // executing.
        if (kept < 1)
        {
            stamped = NavPath.Failed(path.ExpandedNodes);
            return false;
        }

        bool whole = kept == path.Waypoints.Count;
        if (!whole)
        {
            corridor.Clear();
            for (int waypointIndex = 0; waypointIndex < kept; waypointIndex++)
                AddSegmentChunks(path, waypointIndex, corridor);
        }

        var revisions = new NavChunkRevision[corridor.Count];
        int i = 0;
        foreach (var chunk in corridor.OrderBy(chunk => chunk.x).ThenBy(chunk => chunk.z))
            revisions[i++] = new NavChunkRevision(chunk, map.ChunkEditVersion(chunk));

        // Cost is deliberately left describing the route that was PRICED. A trimmed prefix is not
        // re-priced here because nothing compares it against an alternative — it is executed or
        // replaced — and re-summing edge costs would invent a number the search never proved.
        stamped = path with
        {
            Waypoints = whole ? path.Waypoints : path.Waypoints.Take(kept).ToArray(),
            ReachedGoal = whole && path.ReachedGoal,
            CorridorRevisions = revisions,
        };
        return true;
    }

    /// <summary>
    /// Chunks that waypoint <paramref name="waypointIndex"/> and the segment leading to it occupy.
    /// One definition, so stamping and trimming can never disagree about what a route depends on.
    /// </summary>
    private static void AddSegmentChunks(
        NavPath path,
        int waypointIndex,
        HashSet<ChunkIndex> chunks)
    {
        var waypoint = path.Waypoints[waypointIndex];
        AddWithApron(waypoint.Cell.X, waypoint.Cell.Z);
        if (waypointIndex == 0) return;

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
            // Collinearity is 3D, and that is load-bearing rather than pedantic. A climb is perfectly
            // collinear in X/Z while doing whatever it likes in Y, so folding on the horizontal
            // projection deleted every tread of a staircase: a six-step ascent became one Walk
            // waypoint EIGHT CELLS overhead — the 4 m horizontal cap admits four steps, and a step
            // may rise two. That is a route no traversal edge would ever have admitted, and the
            // follower cannot execute it; it reports the edge blocked, replans with jumps disabled,
            // and escalates to digging. Actors seen "walking straight up" after a dig left them under
            // an overhang, and squads that re-excavated a staircase already cut for them, were both
            // this. Smoothing must never invent an edge the graph would have refused.
            int firstX = current.Cell.X - previous.Cell.X;
            int firstY = current.Cell.Y - previous.Cell.Y;
            int firstZ = current.Cell.Z - previous.Cell.Z;
            int secondX = next.Cell.X - current.Cell.X;
            int secondY = next.Cell.Y - current.Cell.Y;
            int secondZ = next.Cell.Z - current.Cell.Z;
            bool sameDirection =
                firstY * secondZ - firstZ * secondY == 0
                && firstZ * secondX - firstX * secondZ == 0
                && firstX * secondY - firstY * secondX == 0
                && firstX * secondX + firstY * secondY + firstZ * secondZ > 0;
            // Measured on the positions the follower actually steers to, so the cap covers the climb
            // as well as the ground it crosses.
            bool withinMaximumSegment =
                Vector3.DistanceSquared(previous.Position, next.Position)
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
