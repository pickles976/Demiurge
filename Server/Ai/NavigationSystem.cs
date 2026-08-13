using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace Demiurge.GameServer;

internal enum NavigationPriority : byte
{
    MissingPath,
    Combat,
    Objective,
    Prefetch,
    Roam,
}

/// <summary>
/// Owns a bounded navigation worker pool. The main server thread only submits immutable requests
/// and drains completed paths; it never waits for A*.
/// </summary>
internal sealed class NavigationSystem : IDisposable
{
    private const float SharedRouteJoinRadius = 24f;

    internal readonly record struct PathResult(
        ushort MobId,
        long RequestId,
        long TerrainVersion,
        NavPath Path,
        long QueueMicroseconds,
        long ElapsedMicroseconds);

    internal readonly record struct TimingSample(
        long QueueMicroseconds,
        long SearchMicroseconds);

    internal readonly record struct Metrics(
        long Requested,
        long Completed,
        long SearchMicroseconds,
        long QueueMicroseconds,
        long ExpandedNodes,
        long ReturnedPathMetres,
        long CacheHits,
        long PartialPaths,
        long CompletePaths,
        long Cancelled,
        long SpatialInvalidations,
        long SpatialTrims,
        long StartChunkInvalidations,
        long SharedRouteReuses,
        long CoalescedTraversalFills);

    private sealed record PathRequest(
        ushort MobId,
        long RequestId,
        NavCell Start,
        INavGoal Goal,
        bool AllowJump,
        bool AllowDig,
        long? BlockedCellKey,
        IReadOnlyList<long> PartialBacktrackCellKeys,
        int PartialBacktrackAttempts,
        long SharedRouteKey,
        NavigationPriority Priority,
        NavCell? PreferredDigSite,
        long EnqueuedTimestamp);

    private readonly record struct RequestTicket(ushort MobId, long RequestId);

    private readonly ChunkMap terrain;
    private readonly NavSearchOptions searchOptions;
    private readonly NavTraversalCache traversalCache = new();
    private readonly object requestGate = new();
    private readonly PriorityQueue<RequestTicket, (int Priority, long Sequence)> requestOrder = new();
    private readonly Dictionary<ushort, PathRequest> pendingByMob = new();
    private readonly Dictionary<ushort, long> latestRequestByMob = new();
    private readonly object sharedRouteGate = new();
    private readonly Dictionary<long, NavPath> sharedRoutes = new();
    private readonly ConcurrentQueue<PathResult> completed = new();
    private readonly ConcurrentQueue<TimingSample> timingSamples = new();
    private readonly SemaphoreSlim requestReady = new(0);
    private readonly Thread[] workers;

    private bool stopping;
    private long nextRequestId;
    private long requestedCount;
    private long completedCount;
    private long searchMicroseconds;
    private long queueMicroseconds;
    private long expandedNodes;
    private long returnedPathMetres;
    private long cacheHits;
    private long partialPaths;
    private long completePaths;
    private long cancelledCount;
    private long spatialInvalidations;
    private long spatialTrims;
    private long startChunkInvalidations;
    private long sharedRouteReuses;
    private long enqueueSequence;

    /// <summary>
    /// How many search threads the pool runs by default.
    ///
    /// Half the logical processors, capped at eight. Cutting it to a quarter capped at four was
    /// tried, on the reasoning that the pool wants 118 ms of CPU per 33 ms tick at p50 and 199 ms at
    /// peak on the conquest scenario, and that a six-core reference machine cannot give navigation
    /// the whole box while a tick and a renderer hold the hard deadlines.
    ///
    /// It measured WORSE, and unambiguously: the conquest capture scenarios pass 4 runs of 4 at eight
    /// workers and fail 3 of 4 at four. The pool is not over-provisioned, it is under-served — a
    /// search is expensive enough that eight threads barely keep thirty-two NPCs supplied, and
    /// starving them shows up as NPCs that never reach their objective.
    ///
    /// So this number cannot come down until a search is cheaper or rarer. It is a symptom of
    /// expansion cost, not a lever on it.
    /// </summary>
    public static int DefaultWorkerCount { get; } =
        Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));

    public NavigationSystem(
        ChunkMap terrain,
        NavSearchOptions? searchOptions = null,
        int? workerCount = null)
    {
        this.terrain = terrain;
        this.searchOptions = searchOptions ?? NavSearchOptions.Default;
        int count = workerCount ?? DefaultWorkerCount;
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        workers = new Thread[count];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(Work)
            {
                IsBackground = true,
                Name = $"Demiurge navigation {i + 1}",
            };
            workers[i].Start();
        }
    }

    public int WorkerCount => workers.Length;

    /// <summary>
    /// Enqueues one current request per mob. If that mob is still waiting in the FIFO, this request
    /// replaces it in-place instead of growing the queue.
    /// </summary>
    public long Request(
        ushort mobId,
        NavCell start,
        INavGoal goal,
        bool allowJump = true,
        bool allowDig = false,
        long? blockedCellKey = null,
        IReadOnlyList<long>? partialBacktrackCellKeys = null,
        int partialBacktrackAttempts = 0,
        long sharedRouteKey = 0,
        NavigationPriority priority = NavigationPriority.Objective,
        NavCell? preferredDigSite = null)
    {
        long requestId = Interlocked.Increment(ref nextRequestId);
        lock (requestGate)
        {
            if (stopping) return 0;
            var request = new PathRequest(
                mobId,
                requestId,
                start,
                goal,
                allowJump,
                allowDig,
                blockedCellKey,
                partialBacktrackCellKeys?.ToArray() ?? [],
                partialBacktrackAttempts,
                sharedRouteKey,
                priority,
                preferredDigSite,
                Stopwatch.GetTimestamp());
            requestOrder.Enqueue(
                new RequestTicket(mobId, requestId),
                ((int)priority, Interlocked.Increment(ref enqueueSequence)));
            pendingByMob[mobId] = request;
            latestRequestByMob[mobId] = requestId;
        }

        Interlocked.Increment(ref requestedCount);
        requestReady.Release();
        return requestId;
    }

    public bool TryGetCompleted(out PathResult result)
        => completed.TryDequeue(out result);

    public void Cancel(ushort mobId, long? requestId = null)
    {
        lock (requestGate)
        {
            if (!latestRequestByMob.TryGetValue(mobId, out long latest)
                || requestId is { } expected && latest != expected)
                return;
            latestRequestByMob.Remove(mobId);
            if (pendingByMob.TryGetValue(mobId, out var pending)
                && (requestId is null || pending.RequestId == requestId))
                pendingByMob.Remove(mobId);
        }
    }

    public void DrainTimingSamples(List<long> queueMicroseconds, List<long> searchMicroseconds)
    {
        while (timingSamples.TryDequeue(out var sample))
        {
            queueMicroseconds.Add(sample.QueueMicroseconds);
            searchMicroseconds.Add(sample.SearchMicroseconds);
        }
    }

    public Metrics SnapshotMetrics()
        => new(
            Interlocked.Read(ref requestedCount),
            Interlocked.Read(ref completedCount),
            Interlocked.Read(ref searchMicroseconds),
            Interlocked.Read(ref queueMicroseconds),
            Interlocked.Read(ref expandedNodes),
            Interlocked.Read(ref returnedPathMetres),
            Interlocked.Read(ref cacheHits),
            Interlocked.Read(ref partialPaths),
            Interlocked.Read(ref completePaths),
            Interlocked.Read(ref cancelledCount),
            Interlocked.Read(ref spatialInvalidations),
            Interlocked.Read(ref spatialTrims),
            Interlocked.Read(ref startChunkInvalidations),
            Interlocked.Read(ref sharedRouteReuses),
            traversalCache.CoalescedFills);

    public void Dispose()
    {
        lock (requestGate)
            stopping = true;
        requestReady.Release(workers.Length);
        foreach (var worker in workers)
            worker.Join();
        requestReady.Dispose();
    }

    private void Work()
    {
        while (true)
        {
            requestReady.Wait();
            PathRequest? request;
            lock (requestGate)
            {
                if (stopping) return;
                request = TakeRequest();
            }

            if (request is null)
                continue;
            Process(request);
        }
    }

    private void Process(PathRequest request)
    {
        long terrainVersion = terrain.EditVersion;
        long queueUs = (long)(
            Stopwatch.GetElapsedTime(request.EnqueuedTimestamp).TotalMilliseconds
            * 1000d);
        long started = Stopwatch.GetTimestamp();
        NavPath path;
        bool reusedSharedRoute = TryReuseSharedRoute(request, out path);
        if (reusedSharedRoute)
            Interlocked.Increment(ref sharedRouteReuses);
        if (!reusedSharedRoute)
        {
            int recoveryFailureBudget = request.PartialBacktrackAttempts switch
            {
                >= 12 => 16_384,
                >= 8 => 8_192,
                >= 4 => 4_096,
                >= 1 => 1_024,
                _ => 0,
            };
            int? failureBudget = searchOptions.FailureExpansionBudget;
            var effectiveOptions = searchOptions with
            {
                AllowJump = request.AllowJump,
                // The opening march proves ordinary movement first. Excavation is a recovery
                // action after the follower reaches a real obstruction; admitting speculative dig
                // macros on the spawn request made actors cut down into the base terrain before
                // they had consumed the authored exit. Missing-path/combat requests still dig.
                AllowDig = request.AllowDig
                    && request.Priority != NavigationPriority.Objective,
                // The opening request only has to put an actor onto a useful march. Requiring the
                // full 16 m recovery threshold makes most conquest starts burn the 320-expansion
                // failure budget before returning a perfectly executable opening prefix. Recovery
                // and prefetch keep the larger threshold so repeated local stumps are rejected.
                //
                // Raising this for a STALLED actor was tried on 2026-08-12 and is a regression, for
                // a reason worth keeping: rejecting a partial does not produce a better route, it
                // produces NO route, and no route is no movement. One stalled actor went to 413
                // requests with 384 empty results, and team-wide terrain edits tripled as actors
                // with no path fell back on digging. A short prefix that goes nowhere still beats
                // standing still; the fix for a basin belongs in the global structure that would
                // have routed around it, not in refusing the local answer.
                MinimumPartialDistance = request.Priority == NavigationPriority.Objective
                    ? 3f
                    : searchOptions.MinimumPartialDistance,
                PrimaryExpansionBudget = request.Priority == NavigationPriority.Objective
                    ? Math.Min(searchOptions.PrimaryExpansionBudget ?? 128, 128)
                    : searchOptions.PrimaryExpansionBudget,
                // Most requests still return at the 128-expansion primary boundary. This only
                // raises the hard stop after the actor has consumed several bounded prefixes
                // without leaving the same basin, which is direct evidence that another stump is
                // cheaper to compute but useless to execute.
                FailureExpansionBudget = recoveryFailureBudget > 0
                        ? Math.Max(failureBudget ?? 0, recoveryFailureBudget)
                        : failureBudget,
            };
            path = NavSearch.Find(
                terrain,
                request.Start,
                request.Goal,
                effectiveOptions,
                request.BlockedCellKey,
                () => IsSuperseded(request),
                traversalCache,
                request.PreferredDigSite,
                request.PartialBacktrackCellKeys);
        }
        if (IsSuperseded(request))
        {
            Interlocked.Increment(ref cancelledCount);
            return;
        }
        path = NavPathSmoothing.RemoveCollinearWalks(path);
        if (path.Waypoints.Count > 0)
        {
            int producedWaypoints = path.Waypoints.Count;
            if (!NavPathTerrain.TryStamp(
                    terrain,
                    path,
                    terrainVersion,
                    out path))
            {
                Interlocked.Increment(ref spatialInvalidations);
                if (terrain.ChunkEditVersion(
                        ChunkTransforms.ChunkAt(request.Start.X, request.Start.Z))
                    > terrainVersion)
                    Interlocked.Increment(ref startChunkInvalidations);
                path = NavPath.Failed(path.ExpandedNodes);
            }
            else
            {
                if (path.Waypoints.Count < producedWaypoints)
                    Interlocked.Increment(ref spatialTrims);
                if (!reusedSharedRoute)
                    StoreSharedRoute(request, path);
                Interlocked.Add(
                    ref returnedPathMetres,
                    (long)MathF.Round(PathMetres(path)));
                if (path.ReachedGoal)
                    Interlocked.Increment(ref completePaths);
                else
                    Interlocked.Increment(ref partialPaths);
            }
        }
        long elapsedUs = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000d);

        timingSamples.Enqueue(new TimingSample(queueUs, elapsedUs));
        completed.Enqueue(new PathResult(
            request.MobId,
            request.RequestId,
            terrainVersion,
            path,
            queueUs,
            elapsedUs));
        Interlocked.Increment(ref completedCount);
        Interlocked.Add(ref searchMicroseconds, elapsedUs);
        Interlocked.Add(ref queueMicroseconds, queueUs);
        Interlocked.Add(ref expandedNodes, path.ExpandedNodes);
        Interlocked.Add(ref cacheHits, path.CacheHits);
    }

    private PathRequest? TakeRequest()
    {
        int candidates = requestOrder.Count;
        while (candidates-- > 0
               && requestOrder.TryDequeue(out var ticket, out _))
        {
            if (!pendingByMob.TryGetValue(ticket.MobId, out var request)
                || request.RequestId != ticket.RequestId)
                continue;
            // Incomplete paths are deliberately actor-local. Serialising every request behind a
            // squad key while no complete route exists therefore buys no sharing and turns six
            // independent bounded searches into one long queue. Let them fill all workers; any
            // complete walk-only result is still published atomically for later joiners.
            pendingByMob.Remove(ticket.MobId);
            return request;
        }
        return null;
    }

    private bool IsSuperseded(PathRequest request)
    {
        lock (requestGate)
            return stopping
                || !latestRequestByMob.TryGetValue(request.MobId, out long latest)
                || latest != request.RequestId;
    }

    private static float PathMetres(NavPath path)
    {
        float distance = 0f;
        for (int i = 1; i < path.Waypoints.Count; i++)
            distance += Vector3.Distance(
                path.Waypoints[i - 1].Position,
                path.Waypoints[i].Position);
        return distance;
    }

    private bool TryReuseSharedRoute(PathRequest request, out NavPath path)
    {
        path = NavPath.Failed();
        NavPath? cachedRoute;
        lock (sharedRouteGate)
            sharedRoutes.TryGetValue(request.SharedRouteKey, out cachedRoute);
        // The start cell was sampled on the main thread when this request was enqueued and is being
        // read here, on a worker, some time later — a dig can have taken the ground out from under
        // it in between. The route's own IsValid check does NOT cover that: it compares chunk
        // revisions along the ROUTE, and SharedRouteJoinRadius lets the requester stand well
        // outside that corridor. So the start is validated separately, and a stale one declines the
        // shared route rather than throwing on a thread where an exception kills the process.
        // NavSearch.Find guards its own start the same way, so the ordinary search below still
        // answers this request — with a failure the agent re-issues next tick from a fresh cell.
        if (request.SharedRouteKey == 0
            // Shared trunks contain only ordinary movement. Once the live executor disproves an
            // edge or has begun an excavation, this actor needs its own dig-enabled decision rather
            // than another connector to the same stale air prefix.
            || request.BlockedCellKey is not null
            || request.PreferredDigSite is not null
            || cachedRoute is not { } route
            || !NavTraversal.TryPosition(terrain, request.Start, out var startPosition)
            || !NavPathTerrain.IsValid(terrain, route)
            || route.Waypoints.Count < 2
            || route.Waypoints.Any(waypoint => waypoint.Action == NavAction.Dig))
            return false;
        if (!route.ReachedGoal
            && request.Priority is NavigationPriority.Prefetch or NavigationPriority.MissingPath
            && !TryExtendSharedRoute(request, route, out route))
            return false;

        int closest = -1;
        float closestDistanceSquared = SharedRouteJoinRadius * SharedRouteJoinRadius;
        for (int i = 0; i < route.Waypoints.Count; i++)
        {
            Vector3 delta = route.Waypoints[i].Position - startPosition;
            delta.Y = 0f;
            float distanceSquared = delta.LengthSquared();
            if (distanceSquared >= closestDistanceSquared) continue;
            closest = i;
            closestDistanceSquared = distanceSquared;
        }
        if (closest < 0
            || !route.ReachedGoal && SuffixMetres(route, closest) < 3f)
            return false;

        var entry = route.Waypoints[closest];
        var connector = NavSearch.Find(
            terrain,
            request.Start,
            new GoalNear(entry.Cell, 0.6f),
            NavSearchOptions.Default with
            {
                PrimaryBudget = TimeSpan.FromMilliseconds(10),
                FailureBudget = TimeSpan.FromMilliseconds(30),
                MaximumExpandedNodes = 20_000,
                MinimumPartialDistance = 0f,
                AllowJump = request.AllowJump,
                AllowDig = false,
            },
            request.BlockedCellKey,
            () => IsSuperseded(request),
            traversalCache);
        if (!connector.ReachedGoal)
            return false;

        var waypoints = connector.Waypoints.ToList();
        for (int i = closest; i < route.Waypoints.Count; i++)
        {
            var waypoint = route.Waypoints[i];
            if (i == closest)
                waypoint = waypoint with { Action = NavAction.Walk };
            if (waypoints.Count > 0 && waypoints[^1].Cell == waypoint.Cell)
                continue;
            waypoints.Add(waypoint);
        }

        // A bounded trunk is still valuable. Let every squad member consume the same prefix and
        // allow the first one near its end to extend the cached corridor; requiring a full route
        // here made every member repeat flat A* across the entire map.
        if (!route.ReachedGoal)
        {
            path = new NavPath(
                waypoints,
                ReachedGoal: false,
                connector.Cost + MathF.Max(0f, route.Cost),
                connector.ExpandedNodes,
                connector.CacheHits);
            return true;
        }

        // The shared path ends at whichever formation slot originally populated the cache. Each
        // member still needs a short local departure to its own requested slot.
        var routeEnd = route.Waypoints[^1].Cell;
        NavPath exit = request.Goal.IsInGoal(routeEnd)
            ? new NavPath([], true, 0f, 0)
            : NavSearch.Find(
                terrain,
                routeEnd,
                request.Goal,
                NavSearchOptions.Default with
                {
                    PrimaryBudget = TimeSpan.FromMilliseconds(10),
                    FailureBudget = TimeSpan.FromMilliseconds(30),
                    MaximumExpandedNodes = 20_000,
                    MinimumPartialDistance = 0f,
                    AllowJump = request.AllowJump,
                    AllowDig = false,
                },
                request.BlockedCellKey,
                () => IsSuperseded(request),
                traversalCache);
        if (!exit.ReachedGoal)
            return false;
        foreach (var waypoint in exit.Waypoints)
        {
            if (waypoints.Count > 0 && waypoints[^1].Cell == waypoint.Cell)
                continue;
            waypoints.Add(waypoint);
        }

        path = new NavPath(
            waypoints,
            ReachedGoal: true,
            connector.Cost + MathF.Max(0f, route.Cost) + exit.Cost,
            connector.ExpandedNodes + exit.ExpandedNodes,
            connector.CacheHits + exit.CacheHits);
        return true;
    }

    private bool TryExtendSharedRoute(
        PathRequest request,
        NavPath route,
        out NavPath extended)
    {
        extended = route;
        long terrainVersion = terrain.EditVersion;
        var routeEnd = route.Waypoints[^1].Cell;
        var segment = NavSearch.Find(
            terrain,
            routeEnd,
            request.Goal,
            searchOptions with
            {
                AllowJump = request.AllowJump,
                AllowDig = request.AllowDig,
            },
            request.BlockedCellKey,
            () => IsSuperseded(request),
            traversalCache);
        if (IsSuperseded(request)
            || segment.Waypoints.Count < 2
            // A dig route is deliberately actor-local. Decline the shared extension so Process
            // runs the normal request and returns that macro only to its executor.
            || segment.Waypoints.Any(waypoint => waypoint.Action == NavAction.Dig))
            return false;

        var combined = route.Waypoints.ToList();
        combined.AddRange(segment.Waypoints.Skip(1));
        var candidate = NavPathSmoothing.RemoveCollinearWalks(new NavPath(
            combined,
            segment.ReachedGoal,
            route.Cost + segment.Cost,
            route.ExpandedNodes + segment.ExpandedNodes,
            route.CacheHits + segment.CacheHits));
        if (!NavPathTerrain.TryStamp(
                terrain,
                candidate,
                terrainVersion,
                out extended))
            return false;

        lock (sharedRouteGate)
            sharedRoutes[request.SharedRouteKey] = extended;
        return true;
    }

    private static float SuffixMetres(NavPath route, int startIndex)
    {
        float distance = 0f;
        for (int i = startIndex + 1; i < route.Waypoints.Count; i++)
            distance += Vector3.Distance(
                route.Waypoints[i - 1].Position,
                route.Waypoints[i].Position);
        return distance;
    }

    private void StoreSharedRoute(PathRequest request, NavPath path)
    {
        if (request.SharedRouteKey == 0
            // A bounded prefix has not proved its continuation around the obstacle that stopped
            // the search. Sharing it makes an entire squad converge on the same trench-wall local
            // minimum; each actor then keeps reconnecting to that trunk instead of consuming the
            // actor-local lateral/dig prefix that would leave it. Complete routes remain safe to
            // share, while traversal geometry is still cached across every independent search.
            || !path.ReachedGoal
            || path.Waypoints.Count < 2
            || path.Waypoints.Any(waypoint => waypoint.Action == NavAction.Dig))
            return;

        lock (sharedRouteGate)
        {
            NavPath candidate = path;
            if (sharedRoutes.TryGetValue(request.SharedRouteKey, out var existing)
                && NavPathTerrain.IsValid(terrain, existing)
                && Vector3.DistanceSquared(
                    existing.Waypoints[^1].Position,
                    path.Waypoints[0].Position) <= 3f * 3f)
            {
                var combined = existing.Waypoints.ToList();
                combined.AddRange(path.Waypoints.Skip(1));
                candidate = NavPathSmoothing.RemoveCollinearWalks(new NavPath(
                    combined,
                    path.ReachedGoal,
                    existing.Cost + path.Cost,
                    existing.ExpandedNodes + path.ExpandedNodes,
                    existing.CacheHits + path.CacheHits));
            }
            else if (sharedRoutes.TryGetValue(request.SharedRouteKey, out existing)
                     && NavPathTerrain.IsValid(terrain, existing)
                     && existing.ReachedGoal
                     && !path.ReachedGoal)
            {
                return;
            }

            if (NavPathTerrain.TryStamp(
                    terrain,
                    candidate,
                    terrain.EditVersion,
                    out var stamped))
                sharedRoutes[request.SharedRouteKey] = stamped;
        }
    }
}
