using System.Numerics;

namespace Demiurge;

/// <summary>
/// Every walkable cell's cheapest route to one fixed destination, solved once instead of searched
/// per actor.
///
/// This is the long-range half of navigation. A* is a good local planner and a poor global one here:
/// measured on conquest, thirty-two NPCs heading for four static flags issued a hundred searches a
/// second, individually re-deriving prefixes of the same handful of journeys, and the budget that
/// stops a search from eating the tick is the same budget that stops it reaching a flag four hundred
/// metres away. A field inverts that — one Dijkstra from the destination answers "which way from
/// here" for every cell at once, in O(1) per actor per tick.
///
/// It also fixes route SHARING rather than working around it. Sharing unproved A* prefixes was tried
/// and abandoned because it marched whole squads into the same local minimum (see NAVIGATION.md). A
/// field cannot do that: it is a global optimum from every cell simultaneously, so there is no
/// unproved trunk to agree on wrongly.
///
/// Three properties are load-bearing:
///
/// - **The currency is <see cref="NavCosts"/> seconds**, the same unit A* prices walking, falling and
///   digging in. A field measured in metres or steps would be a second, incompatible notion of a good
///   route, and the follower would have to reconcile them. Sharing the unit is what lets an actor
///   compare "follow the field" against "search locally" at all.
/// - **Cells are keyed in 3D.** A trench, a tunnel or a bridge puts several standable levels in one
///   column, which the codebase supports deliberately, so a field keyed by (x, z) would silently
///   route actors through the wrong deck.
/// - **It is bounded.** See <see cref="DefaultRadiusMetres"/>: an unbounded solve over a 900 m map is
///   not affordable at the cost of one expansion here, and an actor far outside the bound is not
///   waiting on the field anyway — it has a local search for that.
///
/// What it deliberately does NOT model: excavation, per-actor avoidance, claims, and anything whose
/// answer differs between two actors standing on the same cell. Digging changes the graph rather than
/// an edge's price, so it cannot be baked; the rest are per-actor by definition. Those stay with the
/// local search, which is what the field frees up to do them well.
///
/// Terrain edits invalidate it. <see cref="TerrainVersion"/> records the revision it was solved
/// against; repairing a dirty region incrementally is the intended follow-up, and rebuilding is the
/// interim answer.
/// </summary>
public sealed class NavFlowField
{
    /// <summary>
    /// How far from the destination the solve reaches. Bounded because the solve is not cheap — an
    /// expansion here costs the same standability and step checks A* pays — and because the far half
    /// of a large map contributes nothing an actor there can use before it walks into range.
    /// </summary>
    public const float DefaultRadiusMetres = 250f;

    /// <summary>
    /// Metres between solved cells. The field is a long-range GUIDE — the local search owns the last
    /// stretch — so it does not need metre resolution, and at metre resolution it is not affordable:
    /// measured on conquest, a 500 m field costs 366k-654k cells and 50-91 seconds each, 291 s for
    /// four, at roughly 80-100 MB resident. Quartering the lattice cuts both by about sixteen.
    ///
    /// What is lost is precision, not soundness. A coarse EDGE is still proved by the fine steps
    /// along it (see <see cref="TryCoarseEdge"/>), so the field never claims a route the movement
    /// solver would refuse; it can only miss a detour that does not lie near the straight line
    /// between two lattice points, which is the local planner's job anyway.
    /// </summary>
    public const int DefaultStrideCells = 4;

    /// <summary>
    /// Cost to the destination, and the next cell on the way there.
    ///
    /// The successor is stored rather than recomputed, because "cheapest neighbour" is not the same
    /// question as "the neighbour this cell was relaxed from": on flat ground several neighbours tie,
    /// and recomputing would let two adjacent cells pick each other and stall an actor between them.
    /// </summary>
    public readonly record struct Entry(float Cost, long Next);

    private static readonly (int X, int Z)[] Directions =
    [
        (0, 1), (1, 0), (0, -1), (-1, 0),
        (1, 1), (1, -1), (-1, -1), (-1, 1),
    ];

    private readonly Dictionary<long, Entry> cells;

    private NavFlowField(
        NavCell destination,
        long terrainVersion,
        float radiusMetres,
        int strideCells,
        Dictionary<long, Entry> cells,
        int expanded,
        bool complete)
    {
        Destination = destination;
        TerrainVersion = terrainVersion;
        RadiusMetres = radiusMetres;
        StrideCells = strideCells;
        this.cells = cells;
        Expanded = expanded;
        Complete = complete;
    }

    public NavCell Destination { get; }

    /// <summary>The <c>ChunkMap.EditVersion</c> this was solved against. A field older than the
    /// terrain is stale, and the caller decides whether that matters yet.</summary>
    public long TerrainVersion { get; }

    public float RadiusMetres { get; }

    /// <summary>Metres between solved cells; see <see cref="DefaultStrideCells"/>.</summary>
    public int StrideCells { get; }

    /// <summary>Cells with a route. Not the same as cells within the radius — unreachable ground is
    /// simply absent, which is the answer "there is no way from here".</summary>
    public int Count => cells.Count;

    public int Expanded { get; }

    /// <summary>False when the solve stopped on its node ceiling rather than exhausting what it could
    /// reach. A partial field is still sound everywhere it has an entry.</summary>
    public bool Complete { get; }

    /// <summary>
    /// Seconds of travel from <paramref name="cell"/> to the destination, or null where this field
    /// has no route — outside the radius, unreachable, or never solved.
    /// </summary>
    public float? CostFrom(NavCell cell)
        => cells.TryGetValue(cell.Key, out var entry) ? entry.Cost : null;

    /// <summary>The next cell to walk to, or false at the destination and anywhere without a route.</summary>
    public bool TryNext(NavCell cell, out NavCell next)
    {
        next = default;
        if (!cells.TryGetValue(cell.Key, out var entry) || entry.Next == cell.Key)
            return false;
        next = NavCell.FromKey(entry.Next);
        return true;
    }

    /// <summary>
    /// Finds the solved cell nearest an actor, including actors outside the field radius. The latter
    /// is the important case: the nearest boundary cell is a proved gateway into the objective's
    /// global route, so a bounded local search only has to reach that gateway rather than rediscover
    /// the whole map.
    /// </summary>
    public bool TryNearest(NavCell from, out NavCell nearest)
    {
        nearest = default;
        long bestDistanceSquared = long.MaxValue;
        foreach (long key in cells.Keys)
        {
            var candidate = NavCell.FromKey(key);
            long dx = candidate.X - from.X;
            long dy = candidate.Y - from.Y;
            long dz = candidate.Z - from.Z;
            // Height matters, but less than horizontal approach distance. A bridge and its ground
            // can share X/Z; preferring the actor's deck avoids asking a connector to change level
            // merely to join an otherwise identical trunk.
            long distanceSquared = dx * dx + dz * dz + dy * dy / 4;
            if (distanceSquared >= bestDistanceSquared) continue;
            bestDistanceSquared = distanceSquared;
            nearest = candidate;
        }
        return bestDistanceSquared != long.MaxValue;
    }

    /// <summary>
    /// Revalidates and materializes a field suffix against current terrain. This deliberately works
    /// even when <see cref="TerrainVersion"/> is old: edits outside the suffix do not invalidate a
    /// route, while an edit on its next coarse edge is caught by the same fine-step proof used when
    /// the field was built.
    /// </summary>
    public bool TryRoute(
        ChunkMap map,
        NavCell from,
        out IReadOnlyList<NavWaypoint> waypoints,
        int maximumSteps = 4096)
    {
        var route = new List<NavWaypoint>();
        var probes = new NavProbeCache(map);
        var current = from;
        while (route.Count < maximumSteps && TryNext(current, out var next))
        {
            if (!TryCoarseEdge(probes, current, next, StrideCells, out _)
                || !NavTraversal.TryPosition(map, next, out var position))
            {
                waypoints = [];
                return false;
            }
            route.Add(new NavWaypoint(next, position, NavAction.Walk));
            current = next;
        }

        if (current != Destination)
        {
            waypoints = [];
            return false;
        }
        waypoints = route;
        return true;
    }

    /// <summary>
    /// Solves the field by Dijkstra outward from <paramref name="destination"/>.
    ///
    /// Outward from the goal, with every edge evaluated in the direction an actor will walk it: the
    /// cost of stepping A to B is not the cost of stepping B to A — a drop is cheap and the climb back
    /// up may be impossible — so the relaxation asks <see cref="NavTraversal.TryStep"/> for the
    /// FORWARD edge (neighbour to settled cell) and never assumes symmetry.
    /// </summary>
    public static NavFlowField Build(
        ChunkMap map,
        NavCell destination,
        float radiusMetres = DefaultRadiusMetres,
        int strideCells = DefaultStrideCells,
        int maximumCells = 2_000_000,
        Func<bool>? cancellationRequested = null,
        NavStandabilityCache? standability = null)
    {
        strideCells = Math.Max(1, strideCells);
        var cells = new Dictionary<long, Entry>();
        var open = new NavHeap();
        var probes = new NavProbeCache(map, standability);
        long terrainVersion = map.EditVersion;

        if (!NavTraversal.Standable(probes, destination.X, destination.Y, destination.Z, out _))
            return new NavFlowField(
                destination, terrainVersion, radiusMetres, strideCells, cells, 0, true);

        cells[destination.Key] = new Entry(0f, destination.Key);
        open.EnqueueOrDecrease(destination.Key, 0f);

        float radiusSquared = radiusMetres * radiusMetres;
        int expanded = 0;
        bool complete = true;

        while (open.TryDequeue(out long key))
        {
            if (++expanded > maximumCells
                || (expanded & 1023) == 0 && cancellationRequested?.Invoke() == true)
            {
                complete = false;
                break;
            }

            var settled = NavCell.FromKey(key);
            float settledCost = cells[key].Cost;

            foreach (var direction in Directions)
            {
                int x = settled.X + direction.X * strideCells;
                int z = settled.Z + direction.Z * strideCells;
                float dx = x + 0.5f - (destination.X + 0.5f);
                float dz = z + 0.5f - (destination.Z + 0.5f);
                if (dx * dx + dz * dz > radiusSquared) continue;

                // Search the column the way A* does, so the field admits exactly the cells the local
                // planner would: a neighbour may sit a step up or a drop down from this one. The
                // vertical window scales with the stride, because four metres of ground can rise
                // four times as far as one.
                if (!NavTraversal.TryFindStandable(
                        probes,
                        x,
                        z,
                        settled.Y,
                        NavTraversal.MaximumTraverseCellDelta * strideCells,
                        NavTraversal.MaximumTraverseCellDelta * strideCells,
                        out var neighbour,
                        out _))
                    continue;

                // FORWARD edge: neighbour -> settled, because that is the direction an actor
                // following this field will travel.
                if (!TryCoarseEdge(probes, neighbour, settled, strideCells, out float stepCost))
                    continue;

                float cost = settledCost + stepCost;
                if (cells.TryGetValue(neighbour.Key, out var existing) && existing.Cost <= cost)
                    continue;

                cells[neighbour.Key] = new Entry(cost, key);
                open.EnqueueOrDecrease(neighbour.Key, cost);
            }
        }

        return new NavFlowField(
            destination,
            terrainVersion,
            radiusMetres,
            strideCells,
            cells,
            expanded,
            complete);
    }

    /// <summary>
    /// Whether an actor can walk the straight line from <paramref name="from"/> to
    /// <paramref name="to"/>, and what it costs in <see cref="NavCosts"/> seconds.
    ///
    /// The coarse edge is PROVED by the fine steps along it rather than approximated by a rise test
    /// between its endpoints. That is what keeps a coarse field sound: the cost it reports is the sum
    /// of real single-cell costs, in the same currency the local planner uses, and it can never admit
    /// a four-metre hop over a wall that happens to have matching ground at both ends.
    ///
    /// It walks the line by rounding along the dominant axis, and each intermediate cell resolves its
    /// own standable Y, so a coarse edge follows a ramp instead of assuming a plane.
    /// </summary>
    private static bool TryCoarseEdge(
        NavProbeCache probes,
        NavCell from,
        NavCell to,
        int strideCells,
        out float cost)
    {
        cost = 0f;
        if (strideCells <= 1)
            return NavTraversal.TryStep(probes, from, to, out cost);

        int steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Z - from.Z));
        if (steps == 0) return false;

        var current = from;
        for (int i = 1; i <= steps; i++)
        {
            int x = from.X + (to.X - from.X) * i / steps;
            int z = from.Z + (to.Z - from.Z) * i / steps;

            NavCell next;
            if (i == steps)
            {
                next = to;
            }
            else if (!NavTraversal.TryFindStandable(
                         probes,
                         x,
                         z,
                         current.Y,
                         NavTraversal.MaximumTraverseCellDelta,
                         NavTraversal.MaximumTraverseCellDelta,
                         out next,
                         out _))
            {
                return false;
            }

            if (!NavTraversal.TryStep(probes, current, next, out float stepCost))
                return false;
            cost += stepCost;
            current = next;
        }

        return true;
    }

    /// <summary>
    /// Walks the field from <paramref name="from"/> to the destination, for callers that want an
    /// ordinary waypoint list — a follower, or a test asserting the route is sane.
    ///
    /// The step ceiling is a guard against a malformed field, not an expected limit: a correct field
    /// is acyclic by construction, since every successor has strictly lower cost.
    /// </summary>
    public IReadOnlyList<NavCell> Route(NavCell from, int maximumSteps = 4096)
    {
        var route = new List<NavCell>();
        var cell = from;
        while (route.Count < maximumSteps && TryNext(cell, out var next))
        {
            route.Add(next);
            cell = next;
        }
        return route;
    }
}
