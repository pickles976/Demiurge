using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace Demiurge;

/// <param name="MinimumPartialDistance">
/// Required reduction in the goal heuristic before a partial result may stop at
/// <paramref name="PrimaryBudget"/>. This measures goalward progress, not distance from the start.
/// </param>
public readonly record struct NavSearchOptions(
    TimeSpan PrimaryBudget,
    TimeSpan FailureBudget,
    int MaximumExpandedNodes,
    float MinimumPartialDistance,
    bool AllowJump = true,
    bool AllowDig = false)
{
    /// <summary>
    /// Production expansion budgets; see <see cref="PrimaryExpansionBudget"/>.
    /// </summary>
    /// <remarks>
    /// Time budgets are fallbacks and are ignored when expansion budgets are set.
    /// <para>
    /// Measured conquest defaults are 128/320. Larger limits are reserved for per-actor recovery in
    /// NavigationSystem: making 2,048 unconditional raised the 32-route p95 from ~440 ms to ~2,034 ms.
    /// </para>
    /// <para>
    /// Budgets are multiples of 64 because cancellation is checked every 64 expansions.
    /// </para>
    /// <para>
    /// Expansion limits make cost and route quality deterministic; they do not make expansion cheaper.
    /// </para>
    /// </remarks>
    public static NavSearchOptions Default => new(
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(100),
        100_000,
        16f)
    {
        PrimaryExpansionBudget = 128,
        FailureExpansionBudget = 320,
    };

    /// <summary>
    /// Expansion counts that replace wall-clock budgets when set.
    /// </summary>
    /// <remarks>
    /// Wall-clock limits make results depend on scheduler load. Expansion limits make production and
    /// test results deterministic across machines.
    /// <para>
    /// <see cref="Default"/> sets production limits; tests can use <see cref="Deterministic"/>.
    /// </para>
    /// </remarks>
    public int? PrimaryExpansionBudget { get; init; }

    /// <inheritdoc cref="PrimaryExpansionBudget"/>
    public int? FailureExpansionBudget { get; init; }

    /// <summary>
    /// Heuristic weight in frontier ordering: <c>g + w*h</c>.
    /// </summary>
    /// <remarks>
    /// One is ordinary A*. Values above one expand fewer nodes but may return routes up to
    /// <c>w</c> times optimal.
    /// <para>
    /// Keep the default at one unless weighted A* is measured independently.
    /// </para>
    /// </remarks>
    public float HeuristicWeight { get; init; } = 1f;

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
/// Reuses authoritative traversal answers across independent A* calls. Each answer is stamped with
/// the terrain generation at which it was computed and remains valid while its 3x3 chunk
/// neighbourhood is unchanged. A shovel bite therefore invalidates nearby traversal without making
/// every navigation worker cold. Paths still use chunk-scoped corridor validation before execution.
/// </summary>
public sealed class NavTraversalCache
{
    // Covers the usual between-edit working set and avoids concurrent dictionary growth on cold runs.
    private const int InitialCapacity = 4_096;
    private const int FillGateCount = 1_024;
    private const int MaximumEntries = 600_000;

    internal readonly record struct StandableResult(bool Found, NavCell Cell);
    internal readonly record struct CostResult(bool Found, float Cost);
    internal readonly record struct JumpResult(bool Found, NavCell Landing, float Cost);
    internal readonly record struct DigResult(bool Found, Vector3 Target, float Cost);
    private readonly record struct Entry<T>(T Result, long Generation);
    private readonly record struct NeighbourhoodState(long MapVersion, long LastEdit);

    private readonly object capacityGate = new();
    private readonly object[] fillGates = CreateFillGates();
    private readonly ConcurrentDictionary<(int X, int Z, int AroundY), Entry<StandableResult>>
        standable = new(Environment.ProcessorCount, InitialCapacity);
    private readonly ConcurrentDictionary<(long From, long To), Entry<CostResult>> steps =
        new(Environment.ProcessorCount, InitialCapacity);
    private readonly ConcurrentDictionary<(long From, int Dx, int Dz), Entry<JumpResult>> jumps =
        new(Environment.ProcessorCount, InitialCapacity);
    private readonly ConcurrentDictionary<(long From, int Dx, int Dz), Entry<DigResult>> digs =
        new(Environment.ProcessorCount, InitialCapacity);
    private readonly ConcurrentDictionary<(long From, long To), Entry<bool>> walkEdges =
        new(Environment.ProcessorCount, InitialCapacity);
    private readonly ConcurrentDictionary<ChunkIndex, NeighbourhoodState> neighbourhoodEdits = new();
    private int entryCount;
    private long coalescedFills;

    public long CoalescedFills => Interlocked.Read(ref coalescedFills);

    internal object FillGate(int operation, int keyHash)
        => fillGates[HashCode.Combine(operation, keyHash) & (FillGateCount - 1)];

    internal void RecordCoalescedFill() => Interlocked.Increment(ref coalescedFills);

    internal static long Begin(ChunkMap map) => map.EditVersion;

    internal bool TryGetStandable(
        ChunkMap map,
        (int X, int Z, int AroundY) key,
        out StandableResult result)
    {
        result = default;
        if (!standable.TryGetValue(key, out var entry)
            || !IsValid(map, entry.Generation, key.X, key.Z, out long validatedGeneration))
            return false;
        result = entry.Result;
        Promote(standable, key, entry, validatedGeneration);
        return true;
    }

    internal void StoreStandable(
        ChunkMap map,
        long startedGeneration,
        (int X, int Z, int AroundY) key,
        StandableResult result)
    {
        if (IsValid(map, startedGeneration, key.X, key.Z, out _))
            Publish(standable, key, new Entry<StandableResult>(result, startedGeneration));
    }

    internal bool TryGetStep(
        ChunkMap map,
        (long From, long To) key,
        out CostResult result)
    {
        result = default;
        var from = NavCell.FromKey(key.From);
        if (!steps.TryGetValue(key, out var entry)
            || !IsValid(map, entry.Generation, from.X, from.Z, out long validatedGeneration))
            return false;
        result = entry.Result;
        Promote(steps, key, entry, validatedGeneration);
        return true;
    }

    internal void StoreStep(
        ChunkMap map,
        long startedGeneration,
        (long From, long To) key,
        CostResult result)
    {
        var from = NavCell.FromKey(key.From);
        if (IsValid(map, startedGeneration, from.X, from.Z, out _))
            Publish(steps, key, new Entry<CostResult>(result, startedGeneration));
    }

    internal bool TryGetJump(
        ChunkMap map,
        (long From, int Dx, int Dz) key,
        out JumpResult result)
    {
        result = default;
        var from = NavCell.FromKey(key.From);
        if (!jumps.TryGetValue(key, out var entry)
            || !IsValid(map, entry.Generation, from.X, from.Z, out long validatedGeneration))
            return false;
        result = entry.Result;
        Promote(jumps, key, entry, validatedGeneration);
        return true;
    }

    internal void StoreJump(
        ChunkMap map,
        long startedGeneration,
        (long From, int Dx, int Dz) key,
        JumpResult result)
    {
        var from = NavCell.FromKey(key.From);
        if (IsValid(map, startedGeneration, from.X, from.Z, out _))
            Publish(jumps, key, new Entry<JumpResult>(result, startedGeneration));
    }

    internal bool TryGetDig(
        ChunkMap map,
        (long From, int Dx, int Dz) key,
        out DigResult result)
    {
        result = default;
        var from = NavCell.FromKey(key.From);
        if (!digs.TryGetValue(key, out var entry)
            || !IsValid(map, entry.Generation, from.X, from.Z, out long validatedGeneration))
            return false;
        result = entry.Result;
        Promote(digs, key, entry, validatedGeneration);
        return true;
    }

    internal void StoreDig(
        ChunkMap map,
        long startedGeneration,
        (long From, int Dx, int Dz) key,
        DigResult result)
    {
        var from = NavCell.FromKey(key.From);
        if (IsValid(map, startedGeneration, from.X, from.Z, out _))
            Publish(digs, key, new Entry<DigResult>(result, startedGeneration));
    }

    internal bool TryGetWalkEdge(
        ChunkMap map,
        (long From, long To) key,
        out bool result)
    {
        result = default;
        var from = NavCell.FromKey(key.From);
        if (!walkEdges.TryGetValue(key, out var entry)
            || !IsValid(map, entry.Generation, from.X, from.Z, out long validatedGeneration))
            return false;
        result = entry.Result;
        Promote(walkEdges, key, entry, validatedGeneration);
        return true;
    }

    internal void StoreWalkEdge(
        ChunkMap map,
        long startedGeneration,
        (long From, long To) key,
        bool result)
    {
        var from = NavCell.FromKey(key.From);
        if (IsValid(map, startedGeneration, from.X, from.Z, out _))
            Publish(walkEdges, key, new Entry<bool>(result, startedGeneration));
    }

    /// <summary>
    /// Publishes a fill or replaces the same key's spatially stale generation. Normal search calls
    /// arrive here under that key's striped fill gate; the concurrent operation also keeps direct
    /// test callers and terrain-edit races safe. The combined table is bounded because chunk-scoped
    /// invalidation no longer periodically clears it for us.
    /// </summary>
    private void Publish<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> cache,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        if (!cache.TryAdd(key, value))
        {
            cache[key] = value;
            return;
        }

        if (Interlocked.Increment(ref entryCount) < MaximumEntries) return;
        lock (capacityGate)
        {
            if (Volatile.Read(ref entryCount) < MaximumEntries) return;
            standable.Clear();
            steps.Clear();
            jumps.Clear();
            digs.Clear();
            walkEdges.Clear();
            Interlocked.Exchange(ref entryCount, 0);
        }
    }

    private bool IsValid(
        ChunkMap map,
        long generation,
        int x,
        int z,
        out long validatedGeneration)
    {
        long mapVersion = map.EditVersion;
        validatedGeneration = mapVersion;
        // This is the overwhelmingly common path, and intentionally does no chunk dictionary work.
        if (mapVersion == generation) return true;

        var centre = ChunkTransforms.ChunkAt(x, z);
        if (neighbourhoodEdits.TryGetValue(centre, out var cached)
            && cached.MapVersion == mapVersion)
            return cached.LastEdit <= generation;

        // Traversal probes range only a few cells (including a simulated jump), while a chunk is
        // sixteen cells wide. One chunk of apron on every side covers the capsule/SDF stencil and
        // every intermediate movement sample, including operations that begin at a chunk border.
        long lastEdit = map.GlobalInvalidationVersion;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                lastEdit = Math.Max(
                    lastEdit,
                    map.ChunkEditVersion(
                        new ChunkIndex { x = centre.x + dx, z = centre.z + dz }));
        neighbourhoodEdits[centre] = new NeighbourhoodState(mapVersion, lastEdit);
        return lastEdit <= generation;
    }

    private static void Promote<TKey, TValue>(
        ConcurrentDictionary<TKey, Entry<TValue>> cache,
        TKey key,
        Entry<TValue> entry,
        long currentGeneration)
        where TKey : notnull
    {
        if (entry.Generation != currentGeneration)
            cache.TryUpdate(
                key,
                new Entry<TValue>(entry.Result, currentGeneration),
                entry);
    }

    private static object[] CreateFillGates()
    {
        var gates = new object[FillGateCount];
        for (int i = 0; i < gates.Length; i++) gates[i] = new object();
        return gates;
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
        private readonly NavProbeCache probes;
        private readonly Dictionary<(int X, int Z, int AroundY), NavCell?> standable = [];
        private readonly Dictionary<(long From, long To), float?> steps = [];
        private readonly Dictionary<(long From, int Dx, int Dz), (NavCell Landing, float Cost)?> jumps = [];
        private readonly Dictionary<(long From, int Dx, int Dz), (Vector3 Target, float Cost)?> digs = [];
        private readonly Dictionary<(long From, long To), bool> walkEdges = [];

        public TraversalCache(
            NavTraversalCache? shared,
            long sharedGeneration,
            NavProbeCache probes)
        {
            this.shared = shared;
            this.sharedGeneration = sharedGeneration;
            this.probes = probes;
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
                && shared.TryGetStandable(map, key, out var sharedResult))
            {
                Hits++;
                standable[key] = sharedResult.Found ? sharedResult.Cell : null;
                cell = sharedResult.Cell;
                return sharedResult.Found;
            }

            if (shared is not null)
            {
                lock (shared.FillGate(0, key.GetHashCode()))
                {
                    if (shared.TryGetStandable(map, key, out sharedResult))
                    {
                        shared.RecordCoalescedFill();
                        Hits++;
                        standable[key] = sharedResult.Found ? sharedResult.Cell : null;
                        cell = sharedResult.Cell;
                        return sharedResult.Found;
                    }
                    var computed = Compute();
                    cell = computed.Cell;
                    return computed.Found;
                }
            }

            var uncached = Compute();
            cell = uncached.Cell;
            return uncached.Found;

            (bool Found, NavCell Cell) Compute()
            {
                bool found = NavTraversal.TryFindStandable(
                    probes,
                    x,
                    z,
                    aroundY,
                    NavTraversal.MaximumTraverseCellDelta,
                    NavTraversal.MaximumTraverseCellDelta,
                    out NavCell computedCell,
                    out _);
                standable[key] = found ? computedCell : null;
                shared?.StoreStandable(
                    map,
                    sharedGeneration,
                    key,
                    new NavTraversalCache.StandableResult(found, computedCell));
                return (found, computedCell);
            }
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
                && shared.TryGetStep(map, key, out var sharedResult))
            {
                Hits++;
                steps[key] = sharedResult.Found ? sharedResult.Cost : null;
                cost = sharedResult.Cost;
                return sharedResult.Found;
            }
            if (shared is not null)
            {
                lock (shared.FillGate(1, key.GetHashCode()))
                {
                    if (shared.TryGetStep(map, key, out sharedResult))
                    {
                        shared.RecordCoalescedFill();
                        Hits++;
                        steps[key] = sharedResult.Found ? sharedResult.Cost : null;
                        cost = sharedResult.Cost;
                        return sharedResult.Found;
                    }
                    var computed = Compute();
                    cost = computed.Cost;
                    return computed.Found;
                }
            }

            var uncached = Compute();
            cost = uncached.Cost;
            return uncached.Found;

            (bool Found, float Cost) Compute()
            {
                bool traversable = NavTraversal.TryStep(
                    probes,
                    from,
                    to,
                    out float computedCost);
                steps[key] = traversable ? computedCost : null;
                shared?.StoreStep(
                    map,
                    sharedGeneration,
                    key,
                    new NavTraversalCache.CostResult(traversable, computedCost));
                return (traversable, computedCost);
            }
        }

        /// <summary>
        /// Uncached on purpose, unlike every other edge here. A fall is a column scan plus a handful
        /// of capsule probes against the memo the whole search shares, not a movement simulation,
        /// and one search asks about a given ledge once. A sixth table would cost more than it saves.
        /// </summary>
        public bool TryFall(NavCell from, int dx, int dz, out NavCell landing, out float cost)
            => NavTraversal.TryFall(probes, from, dx, dz, out landing, out cost);

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
                && shared.TryGetJump(map, key, out var sharedResult))
            {
                Hits++;
                jumps[key] = sharedResult.Found
                    ? (sharedResult.Landing, sharedResult.Cost)
                    : null;
                landing = sharedResult.Landing;
                cost = sharedResult.Cost;
                return sharedResult.Found;
            }
            if (shared is not null)
            {
                lock (shared.FillGate(2, key.GetHashCode()))
                {
                    if (shared.TryGetJump(map, key, out sharedResult))
                    {
                        shared.RecordCoalescedFill();
                        Hits++;
                        jumps[key] = sharedResult.Found
                            ? (sharedResult.Landing, sharedResult.Cost)
                            : null;
                        landing = sharedResult.Landing;
                        cost = sharedResult.Cost;
                        return sharedResult.Found;
                    }
                    var computed = Compute();
                    landing = computed.Landing;
                    cost = computed.Cost;
                    return computed.Found;
                }
            }

            var uncached = Compute();
            landing = uncached.Landing;
            cost = uncached.Cost;
            return uncached.Found;

            (bool Found, NavCell Landing, float Cost) Compute()
            {
                bool traversable = NavTraversal.TryJump(
                    map,
                    from,
                    dx,
                    dz,
                    out NavCell computedLanding,
                    out float computedCost);
                jumps[key] = traversable ? (computedLanding, computedCost) : null;
                shared?.StoreJump(
                    map,
                    sharedGeneration,
                    key,
                    new NavTraversalCache.JumpResult(
                        traversable,
                        computedLanding,
                        computedCost));
                return (traversable, computedLanding, computedCost);
            }
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
                && shared.TryGetWalkEdge(map, key, out bool sharedResult))
            {
                Hits++;
                walkEdges[key] = sharedResult;
                return sharedResult;
            }
            if (shared is not null)
            {
                lock (shared.FillGate(3, key.GetHashCode()))
                {
                    if (shared.TryGetWalkEdge(map, key, out sharedResult))
                    {
                        shared.RecordCoalescedFill();
                        Hits++;
                        walkEdges[key] = sharedResult;
                        return sharedResult;
                    }
                    return Compute();
                }
            }

            return Compute();

            bool Compute()
            {
                bool valid = NavTraversal.CanWalkEdge(map, from, to);
                walkEdges[key] = valid;
                shared?.StoreWalkEdge(map, sharedGeneration, key, valid);
                return valid;
            }
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
                && shared.TryGetDig(map, key, out var sharedResult))
            {
                Hits++;
                digs[key] = sharedResult.Found
                    ? (sharedResult.Target, sharedResult.Cost)
                    : null;
                target = sharedResult.Target;
                cost = sharedResult.Cost;
                return sharedResult.Found;
            }

            if (shared is not null)
            {
                lock (shared.FillGate(4, key.GetHashCode()))
                {
                    if (shared.TryGetDig(map, key, out sharedResult))
                    {
                        shared.RecordCoalescedFill();
                        Hits++;
                        digs[key] = sharedResult.Found
                            ? (sharedResult.Target, sharedResult.Cost)
                            : null;
                        target = sharedResult.Target;
                        cost = sharedResult.Cost;
                        return sharedResult.Found;
                    }
                    var computed = Compute();
                    target = computed.Target;
                    cost = computed.Cost;
                    return computed.Found;
                }
            }

            var uncached = Compute();
            target = uncached.Target;
            cost = uncached.Cost;
            return uncached.Found;

            (bool Found, Vector3 Target, float Cost) Compute()
            {
                bool found = NavTraversal.TryDig(
                    map,
                    from,
                    dx,
                    dz,
                    out Vector3 computedTarget,
                    out float computedCost);
                digs[key] = found ? (computedTarget, computedCost) : null;
                shared?.StoreDig(
                    map,
                    sharedGeneration,
                    key,
                    new NavTraversalCache.DigResult(found, computedTarget, computedCost));
                return (found, computedTarget, computedCost);
            }
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
    private const float MinimumGoalRisePerHorizontalMetreForRecovery = 0.5f;
    // Small authored terraces and rolling ground are ordinary one-way progress on a long route.
    // Treating every one-cell fall as an unproved trench entry made bounded searches incapable of
    // returning a prefix after the first shallow valley. Six cells still catches the six-metre
    // trench cases while letting normal terrain stream through receding-horizon searches.
    private const int MinimumUncommittedDescentCells = 6;

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
        NavCell? preferredDigSite = null,
        IReadOnlyList<long>? partialBacktrackCellKeys = null)
    {
        var options = requestedOptions ?? NavSearchOptions.Default;
        var probes = new NavProbeCache(map);
        if (options.MaximumExpandedNodes <= 0
            || options.PrimaryBudget < TimeSpan.Zero
            || options.FailureBudget < options.PrimaryBudget
            || !NavTraversal.Standable(probes, start.X, start.Y, start.Z, out _))
            return NavPath.Failed();

        var nodes = new Dictionary<long, Node>();
        var open = new NavHeap();
        long sharedGeneration = sharedTraversalCache is null
            ? 0
            : NavTraversalCache.Begin(map);
        var traversal = new TraversalCache(sharedTraversalCache, sharedGeneration, probes);
        var startNode = new Node
        {
            Cell = start,
            Cost = 0f,
            Heuristic = goal.Heuristic(start),
        };
        nodes[start.Key] = startNode;
        float heuristicWeight = options.HeuristicWeight;
        open.EnqueueOrDecrease(start.Key, startNode.Heuristic * heuristicWeight);

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
                    (startHeuristic - best.Heuristic) * NavCosts.HeuristicSpeed
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
                    partialBacktrackCellKeys,
                    preferredDigSite,
                    traversal,
                    nodes,
                    open,
                    digFrontiers,
                    seenDigFrontiers,
                    heuristicWeight);
        }

        // An exhausted ordinary region has no air route, so its best executable dig frontier wins.
        // When the wall-clock/node budget produced a useful air prefix instead, compare the two in
        // estimated execution seconds. This preserves bounded long-route progress and avoids the old
        // unconditional second A* pass.
        float bestAirScore = best.Cost + best.Heuristic;
        bool madeAirProgress =
            (startHeuristic - best.Heuristic) * NavCosts.HeuristicSpeed
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
            // Emptying the ordinary region proves there is no walk route; it does not prove that
            // digging farther down is escapable. Returning such a macro stranded conquest actors
            // below a trench while an authored exit existed outside the local region. Upward and
            // level cuts remain eligible, but an irreversible descent needs a completed route.
            allowUncommittedDeepDescent: false);
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
        IReadOnlyList<long>? partialBacktrackCellKeys,
        NavCell? preferredDigSite,
        TraversalCache traversal,
        Dictionary<long, Node> nodes,
        NavHeap open,
        PriorityQueue<DigFrontier, float> digFrontiers,
        HashSet<(long From, int Dx, int Dz)> seenDigFrontiers,
        float heuristicWeight)
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
            if (IsAvoided(next, blockedCellKey)
                || IsPartialBacktrack(next, partialBacktrackCellKeys))
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
                        open,
                        heuristicWeight);
                // A ledge the walk validator rejected is exactly where a step down belongs, and the
                // jump above may have relaxed the same cell at a different price. Offer both and let
                // A* keep the cheaper one; that is what pricing them in the same unit is for.
                TryRelaxFall(
                    goal, current, dx, dz, blockedCellKey, partialBacktrackCellKeys,
                    traversal, nodes, open, heuristicWeight);
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

            Relax(goal, current, next, edgeCost, NavAction.Walk, nodes, open, heuristicWeight);
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
            if (IsAvoided(next, blockedCellKey)
                || IsPartialBacktrack(next, partialBacktrackCellKeys))
                return;
            Relax(goal, current, next, edgeCost, NavAction.Jump, nodes, open, heuristicWeight);
            return;
        }

        // Nothing to walk onto within the traversable band is the ordinary shape of a ledge: the
        // ground in that direction is further down than a step. Not gated on allowJump — that flag
        // withdraws an athletic move a live follower already failed, and stepping off a roof is not
        // one. Without this edge a man on a roof has no way off it but the stairs he came up.
        //
        // Deliberately does NOT return when it succeeds. Recording an excavation frontier costs
        // nothing and commits nothing, and letting a fall suppress it stopped an actor climbing out
        // of a pit at all: partway up its wall there is always somewhere to drop back to, so the
        // frontier that would have cut the next tread was never offered.
        TryRelaxFall(
            goal, current, dx, dz, blockedCellKey, partialBacktrackCellKeys,
            traversal, nodes, open, heuristicWeight);

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

    /// <summary>
    /// Prices the drop off this edge, if there is one, and relaxes it. Cardinal only, matching the
    /// jump and dig edges: a diagonal step off a corner is a shape the follower cannot aim at.
    /// </summary>
    private static bool TryRelaxFall(
        INavGoal goal,
        Node current,
        int dx,
        int dz,
        long? blockedCellKey,
        IReadOnlyList<long>? partialBacktrackCellKeys,
        TraversalCache traversal,
        Dictionary<long, Node> nodes,
        NavHeap open,
        float heuristicWeight)
    {
        if (dx != 0 && dz != 0) return false;
        if (!traversal.TryFall(current.Cell, dx, dz, out var landing, out float cost))
            return false;
        if (IsAvoided(landing, blockedCellKey)
            || IsPartialBacktrack(landing, partialBacktrackCellKeys))
            return false;
        Relax(goal, current, landing, cost, NavAction.Fall, nodes, open, heuristicWeight);
        return true;
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

    private static bool IsPartialBacktrack(
        NavCell cell,
        IReadOnlyList<long>? partialBacktrackCellKeys)
    {
        if (partialBacktrackCellKeys is not { Count: > 0 }) return false;
        // This is route commitment, not collision evidence. Exclude the consumed anchors exactly:
        // applying the blocked-edge one-cell radius to a trail of prefixes can surround the current
        // cell completely, producing an empty path/request loop instead of an escape.
        foreach (long key in partialBacktrackCellKeys)
            if (cell.Key == key)
                return true;
        return false;
    }

    private static void Relax(
        INavGoal goal,
        Node current,
        NavCell next,
        float edgeCost,
        NavAction action,
        Dictionary<long, Node> nodes,
        NavHeap open,
        float heuristicWeight)
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
        // A bounded partial answer does not commit to a descent deeper than a man steps off on
        // purpose, because it cannot prove there is a way back up and the graph never prices being
        // stranded. The threshold is the fall bound itself, and it is applied consistently to both
        // actions because a jump simulation and a fall probe can discover the SAME landing cell.
        // A* keeps whichever priced it lower; while the two disagreed about commitment, the answer
        // depended on which edge won by a hundredth of a second. It cost an afternoon — every route
        // off the conquest spawn building's roof came back empty, because the jump edge undercut the
        // fall by 0.03 s and dragged the whole region below the roof into "uncommitted", where no
        // partial answer may end. Sixteen NPCs walked to the edge and stood there.
        //
        // Two cells used to be the threshold, from when jumping into a pit was the only way down.
        node.HasUncommittedDeepDescent =
            current.HasUncommittedDeepDescent
            || action is NavAction.Jump or NavAction.Fall
                && (current.Cell.Y - next.Y > NavTraversal.MaximumFallCells
                    || current.Cell.Y - next.Y >= MinimumUncommittedDescentCells
                    && DescentMovesVerticallyAwayFromGoal(goal, current.Cell, next));
        open.EnqueueOrDecrease(next.Key, node.Cost + node.Heuristic * heuristicWeight);
    }

    /// <summary>
    /// A local drop can reduce straight-line distance by moving horizontally toward a target while
    /// making the route categorically worse in Y — the exact shape of stepping into a trench whose
    /// goal is on the far rim. Keep exploring it, because a complete route may prove a real exit,
    /// but do not hand the irreversible prefix to a live follower merely because the search budget
    /// ended first.
    /// </summary>
    private static bool DescentMovesVerticallyAwayFromGoal(
        INavGoal goal,
        NavCell from,
        NavCell next)
    {
        if (next.Y >= from.Y) return false;
        NavCell target = goal switch
        {
            GoalPosition position => position.Target,
            GoalNear near => near.Target,
            _ => default,
        };
        return (goal is GoalPosition || goal is GoalNear)
            && Math.Abs(target.Y - next.Y) > Math.Abs(target.Y - from.Y);
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
