using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace Demiurge;

/// <param name="MinimumPartialDistance">
/// How much closer to the goal a partial answer has to get, in metres, before the search is allowed
/// to stop at <paramref name="PrimaryBudget"/> instead of running on to
/// <paramref name="FailureBudget"/>.
///
/// PROGRESS, not travel. It used to measure how far the best node had got from the START, which a
/// search can satisfy while going nowhere useful: pacing sideways along a trench lip covers the
/// distance without ever getting closer to the far bank, so the search declared a useful partial
/// answer and stopped — pointing the actor at the lip it had been pacing. Measuring the reduction in
/// the goal's own heuristic instead means "useful" means what it says.
/// </param>
public readonly record struct NavSearchOptions(
    TimeSpan PrimaryBudget,
    TimeSpan FailureBudget,
    int MaximumExpandedNodes,
    float MinimumPartialDistance,
    bool AllowJump = true,
    bool AllowDig = false)
{
    public static NavSearchOptions Default => new(
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(100),
        100_000,
        16f);
}

/// <summary>
/// Reuses authoritative traversal answers across independent A* calls. A terrain edit advances the
/// generation and clears the cache; paths still use chunk-scoped corridor validation, so cache
/// invalidation affects search cost rather than stopping unrelated actors.
/// </summary>
public sealed class NavTraversalCache
{
    internal readonly record struct StandableResult(bool Found, NavCell Cell);
    internal readonly record struct CostResult(bool Found, float Cost);
    internal readonly record struct JumpResult(bool Found, NavCell Landing, float Cost);

    private readonly object generationGate = new();
    private readonly ConcurrentDictionary<(int X, int Z, int AroundY), StandableResult>
        standable = [];
    private readonly ConcurrentDictionary<(long From, long To), CostResult> steps = [];
    private readonly ConcurrentDictionary<(long From, int Dx, int Dz), JumpResult> jumps = [];
    private readonly ConcurrentDictionary<(long From, long To), bool> walkEdges = [];
    private long generation = long.MinValue;

    internal long Begin(long terrainVersion)
    {
        if (Volatile.Read(ref generation) == terrainVersion)
            return terrainVersion;
        lock (generationGate)
        {
            if (generation == terrainVersion) return terrainVersion;
            standable.Clear();
            steps.Clear();
            jumps.Clear();
            walkEdges.Clear();
            Volatile.Write(ref generation, terrainVersion);
            return terrainVersion;
        }
    }

    internal bool TryGetStandable(
        long expectedGeneration,
        (int X, int Z, int AroundY) key,
        out StandableResult result)
    {
        result = default;
        return Volatile.Read(ref generation) == expectedGeneration
            && standable.TryGetValue(key, out result);
    }

    internal void StoreStandable(
        long expectedGeneration,
        (int X, int Z, int AroundY) key,
        StandableResult result)
    {
        if (Volatile.Read(ref generation) == expectedGeneration)
            standable.TryAdd(key, result);
    }

    internal bool TryGetStep(
        long expectedGeneration,
        (long From, long To) key,
        out CostResult result)
    {
        result = default;
        return Volatile.Read(ref generation) == expectedGeneration
            && steps.TryGetValue(key, out result);
    }

    internal void StoreStep(
        long expectedGeneration,
        (long From, long To) key,
        CostResult result)
    {
        if (Volatile.Read(ref generation) == expectedGeneration)
            steps.TryAdd(key, result);
    }

    internal bool TryGetJump(
        long expectedGeneration,
        (long From, int Dx, int Dz) key,
        out JumpResult result)
    {
        result = default;
        return Volatile.Read(ref generation) == expectedGeneration
            && jumps.TryGetValue(key, out result);
    }

    internal void StoreJump(
        long expectedGeneration,
        (long From, int Dx, int Dz) key,
        JumpResult result)
    {
        if (Volatile.Read(ref generation) == expectedGeneration)
            jumps.TryAdd(key, result);
    }

    internal bool TryGetWalkEdge(
        long expectedGeneration,
        (long From, long To) key,
        out bool result)
    {
        result = default;
        return Volatile.Read(ref generation) == expectedGeneration
            && walkEdges.TryGetValue(key, out result);
    }

    internal void StoreWalkEdge(
        long expectedGeneration,
        (long From, long To) key,
        bool result)
    {
        if (Volatile.Read(ref generation) == expectedGeneration)
            walkEdges.TryAdd(key, result);
    }
}

/// <summary>
/// Bounded deterministic A*. Wall-clock checks are amortized every 64 expansions and any bounded
/// search can return its best useful prefix instead of blocking a server tick for a perfect path.
/// </summary>
public static class NavSearch
{
    private sealed class TraversalCache
    {
        private readonly NavTraversalCache? shared;
        private readonly long sharedGeneration;
        private readonly Dictionary<(int X, int Z, int AroundY), NavCell?> standable = [];
        private readonly Dictionary<(long From, long To), float?> steps = [];
        private readonly Dictionary<(long From, int Dx, int Dz), (NavCell Landing, float Cost)?> jumps = [];
        private readonly Dictionary<(long From, long To), bool> walkEdges = [];

        public TraversalCache(NavTraversalCache? shared, long sharedGeneration)
        {
            this.shared = shared;
            this.sharedGeneration = sharedGeneration;
        }

        public int Hits { get; private set; }

        public bool TryFindStandable(
            ChunkMap map,
            int x,
            int z,
            int aroundY,
            out NavCell cell)
        {
            var key = (x, z, aroundY);
            if (standable.TryGetValue(key, out var cached))
            {
                Hits++;
                cell = cached.GetValueOrDefault();
                return cached.HasValue;
            }
            if (shared is not null
                && shared.TryGetStandable(sharedGeneration, key, out var sharedResult))
            {
                Hits++;
                standable[key] = sharedResult.Found ? sharedResult.Cell : null;
                cell = sharedResult.Cell;
                return sharedResult.Found;
            }

            bool found = NavTraversal.TryFindStandable(
                map,
                x,
                z,
                aroundY,
                NavTraversal.MaximumTraverseCellDelta,
                NavTraversal.MaximumTraverseCellDelta,
                out cell,
                out _);
            standable[key] = found ? cell : null;
            shared?.StoreStandable(
                sharedGeneration,
                key,
                new NavTraversalCache.StandableResult(found, cell));
            return found;
        }

        public bool TryStep(ChunkMap map, NavCell from, NavCell to, out float cost)
        {
            var key = (from.Key, to.Key);
            if (steps.TryGetValue(key, out float? cached))
            {
                Hits++;
                cost = cached ?? NavCosts.Inf;
                return cached.HasValue;
            }
            if (shared is not null
                && shared.TryGetStep(sharedGeneration, key, out var sharedResult))
            {
                Hits++;
                steps[key] = sharedResult.Found ? sharedResult.Cost : null;
                cost = sharedResult.Cost;
                return sharedResult.Found;
            }
            bool traversable = NavTraversal.TryStep(map, from, to, out cost);
            steps[key] = traversable ? cost : null;
            shared?.StoreStep(
                sharedGeneration,
                key,
                new NavTraversalCache.CostResult(traversable, cost));
            return traversable;
        }

        public bool TryJump(
            ChunkMap map,
            NavCell from,
            int dx,
            int dz,
            out NavCell landing,
            out float cost)
        {
            var key = (from.Key, dx, dz);
            if (jumps.TryGetValue(key, out var cached))
            {
                Hits++;
                landing = cached?.Landing ?? default;
                cost = cached?.Cost ?? NavCosts.Inf;
                return cached.HasValue;
            }
            if (shared is not null
                && shared.TryGetJump(sharedGeneration, key, out var sharedResult))
            {
                Hits++;
                jumps[key] = sharedResult.Found
                    ? (sharedResult.Landing, sharedResult.Cost)
                    : null;
                landing = sharedResult.Landing;
                cost = sharedResult.Cost;
                return sharedResult.Found;
            }
            bool traversable = NavTraversal.TryJump(
                map,
                from,
                dx,
                dz,
                out landing,
                out cost);
            jumps[key] = traversable ? (landing, cost) : null;
            shared?.StoreJump(
                sharedGeneration,
                key,
                new NavTraversalCache.JumpResult(traversable, landing, cost));
            return traversable;
        }

        public bool CanWalkEdge(ChunkMap map, NavCell from, NavCell to)
        {
            var key = (from.Key, to.Key);
            if (walkEdges.TryGetValue(key, out bool cached))
            {
                Hits++;
                return cached;
            }
            if (shared is not null
                && shared.TryGetWalkEdge(sharedGeneration, key, out bool sharedResult))
            {
                Hits++;
                walkEdges[key] = sharedResult;
                return sharedResult;
            }
            bool valid = NavTraversal.CanWalkEdge(map, from, to);
            walkEdges[key] = valid;
            shared?.StoreWalkEdge(sharedGeneration, key, valid);
            return valid;
        }
    }

    private sealed class Node
    {
        public required NavCell Cell { get; init; }
        public float Cost = NavCosts.Inf;
        public float Heuristic;
        public long Parent;
        public NavAction ActionFromParent;
        public bool HasParent;
        public bool Closed;
    }

    private readonly record struct DigCandidate(
        Node From,
        NavCell BlockedCell,
        Vector3 Target,
        float Cost,
        float Score);

    private static readonly (int X, int Z)[] Directions =
    [
        (0, 1),
        (1, 0),
        (0, -1),
        (-1, 0),
        (1, 1),
        (1, -1),
        (-1, -1),
        (-1, 1),
    ];

    /// <summary>
    /// Whether an air-only result justifies paying for a second, dig-allowed search. Exhausting the
    /// reachable region without reaching the goal is the test, not distance covered: a bounded prefix
    /// of a good long route is exactly what the budget is supposed to return and must not escalate,
    /// while an actor pacing a trench floor it cannot climb out of covers ground while getting
    /// nowhere and empties its open set doing it.
    /// </summary>
    public static bool NeedsDigEscalation(NavPath airOnly)
        => !airOnly.ReachedGoal
           && (airOnly.Waypoints.Count == 0 || airOnly.ExhaustedReachable);

    /// <summary>
    /// How far from an established excavation a dig frontier still counts as the same site, and how
    /// many seconds of score continuing that site is worth. Without this an NPC took one or two bites
    /// and wandered off to start a fresh hole elsewhere: every bite changes the terrain, which
    /// invalidates the path and forces a fresh search whose best frontier is recomputed from scratch,
    /// so a marginally better cut somewhere else won each time and nothing ever got finished.
    /// </summary>
    public const float DigSiteRadius = 1.1f;
    public static readonly float DigSiteCommitmentSeconds = 2f * NavCosts.DigOneVoxel;

    public static NavPath Find(
        ChunkMap map,
        NavCell start,
        INavGoal goal,
        NavSearchOptions? requestedOptions = null,
        long? blockedCellKey = null,
        Func<bool>? cancellationRequested = null,
        NavTraversalCache? sharedTraversalCache = null,
        NavCell? preferredDigSite = null)
    {
        var options = requestedOptions ?? NavSearchOptions.Default;
        if (options.MaximumExpandedNodes <= 0
            || options.PrimaryBudget < TimeSpan.Zero
            || options.FailureBudget < options.PrimaryBudget
            || !NavTraversal.Standable(map, start.X, start.Y, start.Z, out _))
            return NavPath.Failed();

        var nodes = new Dictionary<long, Node>();
        var open = new NavHeap();
        long sharedGeneration = sharedTraversalCache?.Begin(map.EditVersion) ?? 0;
        var traversal = new TraversalCache(sharedTraversalCache, sharedGeneration);
        var startNode = new Node
        {
            Cell = start,
            Cost = 0f,
            Heuristic = goal.Heuristic(start),
        };
        nodes[start.Key] = startNode;
        open.EnqueueOrDecrease(start.Key, startNode.Heuristic);

        Node best = startNode;
        float startHeuristic = startNode.Heuristic;
        DigCandidate? bestDig = null;
        int expanded = 0;
        long started = Stopwatch.GetTimestamp();
        long primaryTicks = BudgetTicks(options.PrimaryBudget);
        long failureTicks = BudgetTicks(options.FailureBudget);

        // Whether the open set emptied on its own. "I explored everywhere I can reach and the goal
        // is not among it" is a categorically different answer from "I ran out of budget", and only
        // the first one means ordinary movement can never get there.
        bool exhaustedReachable = true;

        while (open.TryDequeue(out long key))
        {
            var current = nodes[key];
            if (current.Closed) continue;
            current.Closed = true;
            expanded++;

            if (goal.IsInGoal(current.Cell))
                return Reconstruct(
                    map,
                    nodes,
                    current,
                    reachedGoal: true,
                    expanded,
                    traversal.Hits);

            if (current.Heuristic < best.Heuristic)
                best = current;

            if (expanded >= options.MaximumExpandedNodes)
            {
                exhaustedReachable = false;
                break;
            }

            if ((expanded & 63) == 0)
            {
                if (cancellationRequested?.Invoke() == true)
                    return NavPath.Failed(expanded) with { CacheHits = traversal.Hits };
                long elapsed = Stopwatch.GetTimestamp() - started;

                // Heuristics are in SECONDS (distance / MaxSpeed), so the reduction converts back to
                // metres before it is compared. Using the goal's own heuristic rather than a
                // straight-line distance keeps this meaningful for every goal kind, including
                // GoalAwayFrom, where "closer" means the opposite direction.
                bool usefulPartial =
                    (startHeuristic - best.Heuristic) * NavCosts.MaxSpeed
                        >= options.MinimumPartialDistance;
                if (elapsed >= failureTicks || elapsed >= primaryTicks && usefulPartial)
                {
                    exhaustedReachable = false;
                    break;
                }
            }

            foreach (var direction in Directions)
                VisitNeighbour(
                    map,
                    goal,
                    current,
                    direction.X,
                    direction.Z,
                    options.AllowJump,
                    options.AllowDig,
                    blockedCellKey,
                    preferredDigSite,
                    traversal,
                    nodes,
                    open,
                    ref bestDig);
        }

        // Reaching the goal already returned above, so getting here with digging enabled means walking
        // cannot finish the route and the best bite is the answer. A dig deliberately does NOT compete
        // on f: it is a single frontier bite, so it costs seconds while buying at most a metre of
        // heuristic, and no cost comparison would ever choose one. Whether digging is on the table at
        // all is decided one layer up by NeedsDigEscalation, which only escalates once an air-only
        // pass has exhausted every cell ordinary movement can reach. Score still ranks bites against
        // each other: cheapest to walk to, closest to the goal once cut.
        if (bestDig is { } dig)
            return ReconstructDig(map, nodes, dig, expanded, traversal.Hits)
                with { ExhaustedReachable = exhaustedReachable };

        return best.Cell == start
            ? NavPath.Failed(expanded) with
            {
                CacheHits = traversal.Hits,
                ExhaustedReachable = exhaustedReachable,
            }
            : Reconstruct(
                map,
                nodes,
                best,
                reachedGoal: false,
                expanded,
                traversal.Hits)
                with { ExhaustedReachable = exhaustedReachable };
    }

    private static void VisitNeighbour(
        ChunkMap map,
        INavGoal goal,
        Node current,
        int dx,
        int dz,
        bool allowJump,
        bool allowDig,
        long? blockedCellKey,
        NavCell? preferredDigSite,
        TraversalCache traversal,
        Dictionary<long, Node> nodes,
        NavHeap open,
        ref DigCandidate? bestDig)
    {
        int x = current.Cell.X + dx;
        int z = current.Cell.Z + dz;
        float edgeCost = NavCosts.Inf;
        bool traversable = traversal.TryFindStandable(
                map,
                x,
                z,
                current.Cell.Y,
                out var next)
            && traversal.TryStep(map, current.Cell, next, out edgeCost);

        if (traversable)
        {
            if (next.Key == blockedCellKey)
                return;
            if (dx != 0 && dz != 0
                && (!CardinalClear(map, current.Cell, dx, 0, traversal)
                    || !CardinalClear(map, current.Cell, 0, dz, traversal))
                && !traversal.CanWalkEdge(map, current.Cell, next))
                return;

            if (NavTraversal.NeedsWalkValidation(map, current.Cell, next)
                && !traversal.CanWalkEdge(map, current.Cell, next))
            {
                if (allowJump
                    && (dx == 0 || dz == 0)
                    && traversal.TryJump(
                        map,
                        current.Cell,
                        dx,
                        dz,
                        out var landing,
                        out float jumpCost))
                    Relax(
                        goal,
                        current,
                        landing,
                        jumpCost,
                        NavAction.Jump,
                        nodes,
                        open);
                return;
            }

            Relax(goal, current, next, edgeCost, NavAction.Walk, nodes, open);
            return;
        }

        if (allowJump
            && (dx == 0 || dz == 0)
            && traversal.TryJump(
                map,
                current.Cell,
                dx,
                dz,
                out next,
                out edgeCost))
        {
            if (next.Key == blockedCellKey)
                return;
            Relax(goal, current, next, edgeCost, NavAction.Jump, nodes, open);
            return;
        }

        if (!allowDig
            || dx != 0 && dz != 0
            || !NavTraversal.TryDig(
                map,
                current.Cell,
                dx,
                dz,
                out var target,
                out edgeCost))
            return;

        var blocked = new NavCell(
            current.Cell.X + dx,
            current.Cell.Y,
            current.Cell.Z + dz);
        float candidateCost = current.Cost + edgeCost;
        float score = candidateCost + goal.Heuristic(blocked);
        // Finishing a cut already started beats opening a better-scoring one somewhere else. The
        // frontier cell itself moves as the cut advances, so commitment is to the SITE, not the cell.
        // An excavation site is a horizontal cut, not one particular Y sample. Staircase digging
        // deliberately targets headroom several cells above the tread; including that vertical
        // separation made the next replan decide its own staircase was outside the commitment
        // radius and open a fresh cut on another wall. The result was a ring of alcoves at floor
        // height rather than one route to the surface.
        if (preferredDigSite is { } site
            && HorizontalDistanceSquared(blocked, site) <= DigSiteRadius * DigSiteRadius)
            score -= DigSiteCommitmentSeconds;
        if (bestDig is null || score < bestDig.Value.Score)
            bestDig = new DigCandidate(current, blocked, target, candidateCost, score);
    }

    private static float HorizontalDistanceSquared(NavCell a, NavCell b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static void Relax(
        INavGoal goal,
        Node current,
        NavCell next,
        float edgeCost,
        NavAction action,
        Dictionary<long, Node> nodes,
        NavHeap open)
    {
        float candidateCost = current.Cost + edgeCost;
        if (candidateCost >= NavCosts.Inf) return;
        if (!nodes.TryGetValue(next.Key, out var node))
        {
            node = new Node
            {
                Cell = next,
                Heuristic = goal.Heuristic(next),
            };
            nodes[next.Key] = node;
        }
        if (node.Closed || candidateCost >= node.Cost) return;

        node.Cost = candidateCost;
        node.Parent = current.Cell.Key;
        node.ActionFromParent = action;
        node.HasParent = true;
        open.EnqueueOrDecrease(next.Key, node.Cost + node.Heuristic);
    }

    private static bool CardinalClear(
        ChunkMap map,
        NavCell current,
        int dx,
        int dz,
        TraversalCache traversal)
        => traversal.TryFindStandable(
               map,
               current.X + dx,
               current.Z + dz,
               current.Y,
               out var adjacent)
           && traversal.TryStep(map, current, adjacent, out _);

    private static NavPath Reconstruct(
        ChunkMap map,
        Dictionary<long, Node> nodes,
        Node end,
        bool reachedGoal,
        int expanded,
        int cacheHits)
    {
        var reversed = new List<NavWaypoint>();
        var node = end;
        while (true)
        {
            // Terrain edits can land while a background search is running. A node that was
            // standable when expanded may therefore cease to be standable before reconstruction.
            // Treat that result as stale and let the navigation agent re-request it; calling
            // NavTraversal.Position here would throw on the worker thread and terminate the game.
            if (!NavTraversal.Standable(
                    map,
                    node.Cell.X,
                    node.Cell.Y,
                    node.Cell.Z,
                    out float surfaceY))
                return NavPath.Failed(expanded) with { CacheHits = cacheHits };
            reversed.Add(new NavWaypoint(
                node.Cell,
                new Vector3(node.Cell.X + 0.5f, surfaceY, node.Cell.Z + 0.5f),
                node.ActionFromParent));
            if (!node.HasParent) break;
            node = nodes[node.Parent];
        }
        reversed.Reverse();
        return new NavPath(reversed, reachedGoal, end.Cost, expanded, cacheHits);
    }

    private static NavPath ReconstructDig(
        ChunkMap map,
        Dictionary<long, Node> nodes,
        DigCandidate dig,
        int expanded,
        int cacheHits)
    {
        var prefix = Reconstruct(
            map,
            nodes,
            dig.From,
            reachedGoal: false,
            expanded,
            cacheHits);
        if (prefix.Waypoints.Count == 0)
            return prefix;
        var waypoints = prefix.Waypoints.ToList();
        waypoints.Add(new NavWaypoint(dig.BlockedCell, dig.Target, NavAction.Dig));
        return new NavPath(waypoints, false, dig.Cost, expanded, cacheHits);
    }

    private static long BudgetTicks(TimeSpan budget)
        => (long)Math.Ceiling(budget.TotalSeconds * Stopwatch.Frequency);
}
