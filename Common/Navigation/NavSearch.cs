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

    /// <summary>
    /// Expansion counts that REPLACE the wall-clock budgets when set.
    /// </summary>
    /// <remarks>
    /// A wall-clock budget makes the search's answer depend on how busy the machine is. Eight workers
    /// on six cores means a descheduled thread blows straight through 25 ms and only notices at its
    /// next 64-expansion check — so the same request returns a complete route on an idle box and a
    /// partial one under load. That is a real production problem (see the note at the top of
    /// docs/TODO.md) and it makes navigation TESTS non-deterministic, which is worse than it sounds: a
    /// suite that fails one run in five cannot tell a regression from noise.
    /// <para>
    /// Counting expansions instead removes the machine from the answer entirely. Set these in tests so
    /// a scenario either passes or fails on its merits. Production still runs on the wall clock,
    /// because switching it changes NPC behaviour and is a design decision rather than a test fix.
    /// </para>
    /// </remarks>
    public int? PrimaryExpansionBudget { get; init; }

    /// <inheritdoc cref="PrimaryExpansionBudget"/>
    public int? FailureExpansionBudget { get; init; }

    /// <summary>Budgets by expansion count, so the result does not depend on machine load.</summary>
    public static NavSearchOptions Deterministic(
        int primaryExpansions = 20_000,
        int failureExpansions = 100_000,
        float minimumPartialDistance = 16f)
        => Default with
        {
            PrimaryExpansionBudget = primaryExpansions,
            FailureExpansionBudget = failureExpansions,
            MinimumPartialDistance = minimumPartialDistance,
        };
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
    internal readonly record struct DigResult(bool Found, Vector3 Target, float Cost);

    private readonly object generationGate = new();
    private readonly ConcurrentDictionary<(int X, int Z, int AroundY), StandableResult>
        standable = [];
    private readonly ConcurrentDictionary<(long From, long To), CostResult> steps = [];
    private readonly ConcurrentDictionary<(long From, int Dx, int Dz), JumpResult> jumps = [];
    private readonly ConcurrentDictionary<(long From, int Dx, int Dz), DigResult> digs = [];
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
            digs.Clear();
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

    internal bool TryGetDig(
        long expectedGeneration,
        (long From, int Dx, int Dz) key,
        out DigResult result)
    {
        result = default;
        return Volatile.Read(ref generation) == expectedGeneration
            && digs.TryGetValue(key, out result);
    }

    internal void StoreDig(
        long expectedGeneration,
        (long From, int Dx, int Dz) key,
        DigResult result)
    {
        if (Volatile.Read(ref generation) == expectedGeneration)
            digs.TryAdd(key, result);
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
        private readonly Dictionary<(long From, int Dx, int Dz), (Vector3 Target, float Cost)?> digs = [];
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

        public bool TryDig(
            ChunkMap map,
            NavCell from,
            int dx,
            int dz,
            out Vector3 target,
            out float cost)
        {
            var key = (from.Key, dx, dz);
            if (digs.TryGetValue(key, out var cached))
            {
                Hits++;
                target = cached?.Target ?? default;
                cost = cached?.Cost ?? NavCosts.Inf;
                return cached.HasValue;
            }
            if (shared is not null
                && shared.TryGetDig(sharedGeneration, key, out var sharedResult))
            {
                Hits++;
                digs[key] = sharedResult.Found
                    ? (sharedResult.Target, sharedResult.Cost)
                    : null;
                target = sharedResult.Target;
                cost = sharedResult.Cost;
                return sharedResult.Found;
            }

            bool found = NavTraversal.TryDig(map, from, dx, dz, out target, out cost);
            digs[key] = found ? (target, cost) : null;
            shared?.StoreDig(
                sharedGeneration,
                key,
                new NavTraversalCache.DigResult(found, target, cost));
            return found;
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
        public bool HasUncommittedDeepDescent;
    }

    private readonly record struct DigCandidate(
        Node From,
        NavCell BlockedCell,
        Vector3 Target,
        float Cost,
        float Score);

    private readonly record struct DigFrontier(
        Node From,
        NavCell BlockedCell,
        int Dx,
        int Dz,
        float LowerScore,
        bool VerticalRecovery);

    // Geometry probing is substantially dearer than recording a blocked edge. Resolve only the
    // cheapest frontier candidates after ordinary expansion, with a hard per-search ceiling.
    private const int MaximumDigProbes = 64;
    private const float MinimumAirProgressBeforeFallback = 4f;
    private const int MaximumCommittedPartialDropCells = 2;
    private const float MinimumGoalRisePerHorizontalMetreForRecovery = 0.5f;

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
    /// How far from an established excavation a dig frontier still counts as the same site, and how
    /// many seconds of score continuing that site is worth. Without this an NPC took one or two bites
    /// and wandered off to start a fresh hole elsewhere: every bite changes the terrain, which
    /// invalidates the path and forces a fresh search whose best frontier is recomputed from scratch,
    /// so a marginally better cut somewhere else won each time and nothing ever got finished.
    /// </summary>
    public const float DigSiteRadius = 3f;
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
        Node furthest = startNode;
        float furthestDistanceSquared = 0f;
        float startHeuristic = startNode.Heuristic;
        var digFrontiers = new PriorityQueue<DigFrontier, float>();
        var seenDigFrontiers = new HashSet<(long From, int Dx, int Dz)>();
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
            {
                // Dig probes are lazy macro edges: their score estimates the next locally useful
                // clearance action, while execution commits only one authoritative shovel bite.
                // A complete ordinary route therefore wins whenever it is cheaper in seconds.
                var goalDig = ResolveBestDig(
                    map,
                    goal,
                    preferredDigSite,
                    traversal,
                    digFrontiers,
                    current.Cost,
                    allowUncommittedDeepDescent: false);
                if (goalDig is { } cheaperDig)
                    return ReconstructDig(map, nodes, cheaperDig, expanded, traversal.Hits)
                        with { ExhaustedReachable = false };
                return Reconstruct(
                    map,
                    nodes,
                    current,
                    reachedGoal: true,
                    expanded,
                    traversal.Hits);
            }

            if (!current.HasUncommittedDeepDescent
                && current.Heuristic < best.Heuristic)
                best = current;
            float fromStartDistanceSquared = CellDistanceSquared(current.Cell, start);
            if (!current.HasUncommittedDeepDescent
                && (fromStartDistanceSquared > furthestDistanceSquared
                || fromStartDistanceSquared == furthestDistanceSquared
                    && current.Heuristic < furthest.Heuristic))
            {
                furthest = current;
                furthestDistanceSquared = fromStartDistanceSquared;
            }

            if (expanded >= options.MaximumExpandedNodes)
            {
                exhaustedReachable = false;
                break;
            }

            // After an authoritative bite, receding-horizon execution should finish the same local
            // macro without spending the full failure budget rediscovering the entire reachable
            // region. Site commitment is already represented in candidate cost; resolve it after a
            // small local expansion, and only return when soil is still executable there.
            if (preferredDigSite is not null
                && (expanded & 3) == 0
                && ResolveBestDig(
                    map,
                    goal,
                    preferredDigSite,
                    traversal,
                    digFrontiers,
                    NavCosts.Inf,
                    allowUncommittedDeepDescent: false) is { } committedDig)
                return ReconstructDig(
                    map,
                    nodes,
                    committedDig,
                    expanded,
                    traversal.Hits)
                    with { ExhaustedReachable = false };

            if ((expanded & 63) == 0)
            {
                if (cancellationRequested?.Invoke() == true)
                    return NavPath.Failed(expanded) with { CacheHits = traversal.Hits };

                // Expansion budgets, when set, replace the clock entirely — including the read, so a
                // deterministic search does not even observe elapsed time.
                bool primarySpent;
                bool failureSpent;
                if (options.PrimaryExpansionBudget is { } primaryExpansions
                    && options.FailureExpansionBudget is { } failureExpansions)
                {
                    primarySpent = expanded >= primaryExpansions;
                    failureSpent = expanded >= failureExpansions;
                }
                else
                {
                    long elapsed = Stopwatch.GetTimestamp() - started;
                    primarySpent = elapsed >= primaryTicks;
                    failureSpent = elapsed >= failureTicks;
                }

                // Heuristics are in SECONDS (distance / MaxSpeed), so the reduction converts back to
                // metres before it is compared. Using the goal's own heuristic rather than a
                // straight-line distance keeps this meaningful for every goal kind, including
                // GoalAwayFrom, where "closer" means the opposite direction.
                bool usefulPartial =
                    (startHeuristic - best.Heuristic) * NavCosts.MaxSpeed
                        >= options.MinimumPartialDistance;
                if (failureSpent || primarySpent && usefulPartial)
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
                    digFrontiers,
                    seenDigFrontiers);
        }

        // An exhausted ordinary region has no air route, so its best executable dig frontier wins.
        // When the wall-clock/node budget produced a useful air prefix instead, compare the two in
        // estimated execution seconds. This preserves bounded long-route progress and avoids the old
        // unconditional second A* pass.
        float bestAirScore = best.Cost + best.Heuristic;
        bool madeAirProgress =
            (startHeuristic - best.Heuristic) * NavCosts.MaxSpeed
                >= MathF.Max(
                    MinimumAirProgressBeforeFallback,
                    options.MinimumPartialDistance);
        if (options.AllowDig)
            RecordGoalDirectedDig(
                goal,
                best,
                preferredDigSite,
                digFrontiers,
                seenDigFrontiers);
        var bestDig = ResolveBestDig(
            map,
            goal,
            preferredDigSite,
            traversal,
            digFrontiers,
            // A live follower has already disproved the blocked idealized edge. An incomplete
            // prefix toward that same edge is not a competing route; resolve an executable macro
            // frontier now instead of returning the same stall forever.
            exhaustedReachable || blockedCellKey is not null
                || (!madeAirProgress && GoalNeedsUpwardRecovery(goal, start))
                ? NavCosts.Inf
                : !madeAirProgress && furthest.Cell != start
                    ? furthest.Cost + furthest.Heuristic
                    : bestAirScore,
            allowUncommittedDeepDescent: exhaustedReachable);
        if (bestDig is { } dig)
            return ReconstructDig(map, nodes, dig, expanded, traversal.Hits)
                with { ExhaustedReachable = exhaustedReachable };

        // A detour around a long obstacle can initially move no closer to the goal. If the bounded
        // search found neither measurable heuristic improvement nor executable soil, return its
        // furthest explored air prefix so the next search starts beyond this local minimum. The dig
        // decision above prevents this fallback from turning a diggable trench floor into endless
        // lateral pacing.
        Node partialBest = !madeAirProgress && furthest.Cell != start
            ? furthest
            : best;
        return partialBest.Cell == start
            ? NavPath.Failed(expanded) with
            {
                CacheHits = traversal.Hits,
                ExhaustedReachable = exhaustedReachable,
            }
            : Reconstruct(
                map,
                nodes,
                partialBest,
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
        PriorityQueue<DigFrontier, float> digFrontiers,
        HashSet<(long From, int Dx, int Dz)> seenDigFrontiers)
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
            if (IsAvoided(next, blockedCellKey))
            {
                if (allowDig && (dx == 0 || dz == 0))
                    RecordDigFrontier(
                        goal,
                        current,
                        next,
                        dx,
                        dz,
                        preferredDigSite,
                        digFrontiers,
                        seenDigFrontiers);
                return;
            }
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
                if (allowDig && (dx == 0 || dz == 0))
                    RecordDigFrontier(
                        goal,
                        current,
                        next,
                        dx,
                        dz,
                        preferredDigSite,
                        digFrontiers,
                        seenDigFrontiers);
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
            if (IsAvoided(next, blockedCellKey))
                return;
            Relax(goal, current, next, edgeCost, NavAction.Jump, nodes, open);
            return;
        }

        if (!allowDig || dx != 0 && dz != 0)
            return;

        var blocked = new NavCell(
            current.Cell.X + dx,
            current.Cell.Y,
            current.Cell.Z + dz);
        RecordDigFrontier(
            goal,
            current,
            blocked,
            dx,
            dz,
            preferredDigSite,
            digFrontiers,
            seenDigFrontiers);
    }

    private static void RecordDigFrontier(
        INavGoal goal,
        Node current,
        NavCell blocked,
        int dx,
        int dz,
        NavCell? preferredDigSite,
        PriorityQueue<DigFrontier, float> digFrontiers,
        HashSet<(long From, int Dx, int Dz)> seenDigFrontiers,
        bool verticalRecovery = false)
    {
        float score = current.Cost + NavCosts.DigOneVoxel + goal.Heuristic(blocked);
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
        if (seenDigFrontiers.Add((current.Cell.Key, dx, dz)) || verticalRecovery)
            digFrontiers.Enqueue(
                new DigFrontier(current, blocked, dx, dz, score, verticalRecovery),
                score);
    }

    private static void RecordGoalDirectedDig(
        INavGoal goal,
        Node from,
        NavCell? preferredDigSite,
        PriorityQueue<DigFrontier, float> digFrontiers,
        HashSet<(long From, int Dx, int Dz)> seenDigFrontiers)
    {
        NavCell target = goal switch
        {
            GoalPosition position => position.Target,
            GoalNear near => near.Target,
            _ => default,
        };
        if (goal is not GoalPosition && goal is not GoalNear) return;

        int deltaX = target.X - from.Cell.X;
        int deltaZ = target.Z - from.Cell.Z;
        int deltaY = target.Y - from.Cell.Y;
        if (NeedsUpwardRecovery(deltaX, deltaY, deltaZ))
        {
            RecordVerticalDigFrontiers();
            return;
        }
        int dx;
        int dz;
        if (Math.Abs(deltaX) > Math.Abs(deltaZ))
        {
            dx = Math.Sign(deltaX);
            dz = 0;
        }
        else
        {
            dx = 0;
            dz = Math.Sign(deltaZ);
        }
        if (dx == 0 && dz == 0)
        {
            if (target.Y <= from.Cell.Y) return;
            // The actor can arrive horizontally beneath a raised goal while still inside its cut.
            // Excavation is cardinal, so expose all four local upward/staircase macros and let their
            // executable time costs choose; otherwise a zero X/Z delta leaves no successor at all.
            RecordVerticalDigFrontiers();
            return;
        }
        RecordDigFrontier(
            goal,
            from,
            new NavCell(from.Cell.X + dx, from.Cell.Y, from.Cell.Z + dz),
            dx,
            dz,
            preferredDigSite,
            digFrontiers,
            seenDigFrontiers);

        void RecordVerticalDigFrontiers()
        {
            foreach (var (verticalDx, verticalDz) in (ReadOnlySpan<(int X, int Z)>)[
                         (0, 1), (1, 0), (0, -1), (-1, 0)])
                RecordDigFrontier(
                    goal,
                    from,
                    new NavCell(
                        from.Cell.X + verticalDx,
                        from.Cell.Y,
                        from.Cell.Z + verticalDz),
                    verticalDx,
                    verticalDz,
                    preferredDigSite,
                    digFrontiers,
                    seenDigFrontiers,
                    verticalRecovery: true);
        }
    }

    private static bool GoalNeedsUpwardRecovery(INavGoal goal, NavCell from)
    {
        NavCell target = goal switch
        {
            GoalPosition position => position.Target,
            GoalNear near => near.Target,
            _ => default,
        };
        return (goal is GoalPosition || goal is GoalNear)
            && NeedsUpwardRecovery(
                target.X - from.X,
                target.Y - from.Y,
                target.Z - from.Z);
    }

    private static bool NeedsUpwardRecovery(int deltaX, int deltaY, int deltaZ)
    {
        if (deltaY <= 0) return false;
        float horizontalSquared = deltaX * deltaX + deltaZ * deltaZ;
        float minimumRise = MinimumGoalRisePerHorizontalMetreForRecovery;
        return deltaY * deltaY > minimumRise * minimumRise * horizontalSquared;
    }

    private static DigCandidate? ResolveBestDig(
        ChunkMap map,
        INavGoal goal,
        NavCell? preferredDigSite,
        TraversalCache traversal,
        PriorityQueue<DigFrontier, float> frontiers,
        float scoreCeiling,
        bool allowUncommittedDeepDescent)
    {
        DigCandidate? best = null;
        int probes = 0;
        while (probes < MaximumDigProbes
               && frontiers.TryPeek(out _, out float lowerScore)
               && lowerScore < (best?.Score ?? scoreCeiling))
        {
            var frontier = frontiers.Dequeue();
            probes++;
            if (frontier.From.HasUncommittedDeepDescent
                && !allowUncommittedDeepDescent)
                continue;
            bool found = traversal.TryDig(
                    map,
                    frontier.From.Cell,
                    frontier.Dx,
                    frontier.Dz,
                    out var target,
                    out float edgeCost);
            if (!found && frontier.VerticalRecovery)
            {
                // TryDig answers false for an unstandable source, so this branch is reached by
                // exactly the cell Position would throw on. Same worker-thread contract as
                // Reconstruct below: a frontier the ground has left is stale, not fatal.
                if (!NavTraversal.TryPosition(map, frontier.From.Cell, out Vector3 feet))
                    continue;
                Vector3 nextFeet = feet + new Vector3(frontier.Dx, 0f, frontier.Dz);
                found = NavTraversal.TryDigClearance(
                        map,
                        nextFeet,
                        frontier.Dx,
                        frontier.Dz,
                        out target)
                    && Digging.InReach(feet, target);
                edgeCost = NavCosts.DigOneVoxel + NavCosts.DigTunnelPenalty;
            }
            if (!found)
                continue;

            float cost = frontier.From.Cost + edgeCost;
            float score = cost + goal.Heuristic(frontier.BlockedCell);
            if (preferredDigSite is { } site
                && HorizontalDistanceSquared(frontier.BlockedCell, site)
                    <= DigSiteRadius * DigSiteRadius)
                score -= DigSiteCommitmentSeconds;
            if (score < (best?.Score ?? scoreCeiling))
                best = new DigCandidate(frontier.From, frontier.BlockedCell, target, cost, score);
        }
        return best;
    }

    private static float HorizontalDistanceSquared(NavCell a, NavCell b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static float CellDistanceSquared(NavCell a, NavCell b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool IsAvoided(NavCell cell, long? blockedCellKey)
    {
        if (blockedCellKey is not { } key) return false;
        NavCell blocked = NavCell.FromKey(key);
        // The live capsule disproves a small landing neighbourhood, not only one quantized centre.
        // Without this, consecutive replans alternate between adjacent samples of the same SDF lip
        // and never expose the dig frontier. One cell matches the capsule diameter while preserving
        // nearby authored exits and bridge approaches.
        return Math.Abs(cell.X - blocked.X) <= 1
            && Math.Abs(cell.Y - blocked.Y) <= 1
            && Math.Abs(cell.Z - blocked.Z) <= 1;
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
        node.HasUncommittedDeepDescent =
            current.HasUncommittedDeepDescent
            || action == NavAction.Jump
                && current.Cell.Y - next.Y > MaximumCommittedPartialDropCells;
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
