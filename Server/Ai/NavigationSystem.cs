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
        long SharedRouteReuses);

    private sealed record PathRequest(
        ushort MobId,
        long RequestId,
        NavCell Start,
        INavGoal Goal,
        bool AllowJump,
        bool AllowDig,
        long? BlockedCellKey,
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
    private readonly HashSet<long> activeSharedRouteKeys = [];
    private readonly HashSet<long> sharedRouteOwnerRequests = [];
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
    private long sharedRouteReuses;
    private long enqueueSequence;

    public NavigationSystem(
        ChunkMap terrain,
        NavSearchOptions? searchOptions = null,
        int? workerCount = null)
    {
        this.terrain = terrain;
        this.searchOptions = searchOptions ?? NavSearchOptions.Default;
        int count = workerCount
            ?? Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));
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
            Interlocked.Read(ref sharedRouteReuses));

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
            try
            {
                Process(request);
            }
            finally
            {
                ReleaseSharedRoute(request);
            }
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
            path = NavSearch.Find(
                terrain,
                request.Start,
                request.Goal,
                searchOptions with
                {
                    AllowJump = request.AllowJump,
                    AllowDig = request.AllowDig,
                },
                request.BlockedCellKey,
                () => IsSuperseded(request),
                traversalCache,
                request.PreferredDigSite);
        if (IsSuperseded(request))
        {
            Interlocked.Increment(ref cancelledCount);
            return;
        }
        path = NavPathSmoothing.RemoveCollinearWalks(path);
        if (path.Waypoints.Count > 0)
        {
            if (!NavPathTerrain.TryStamp(
                    terrain,
                    path,
                    terrainVersion,
                    out path))
            {
                Interlocked.Increment(ref spatialInvalidations);
                path = NavPath.Failed(path.ExpandedNodes);
            }
            else
            {
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
        List<(RequestTicket Ticket, (int Priority, long Sequence) Priority)>? deferred = null;
        while (candidates-- > 0
               && requestOrder.TryDequeue(out var ticket, out var priority))
        {
            if (!pendingByMob.TryGetValue(ticket.MobId, out var request)
                || request.RequestId != ticket.RequestId)
                continue;
            bool needsExclusiveSharedRoute =
                request.SharedRouteKey != 0
                && (request.Priority == NavigationPriority.Prefetch
                    || !HasUsableSharedRoute(request.SharedRouteKey));
            if (needsExclusiveSharedRoute
                && activeSharedRouteKeys.Contains(request.SharedRouteKey))
            {
                (deferred ??= []).Add((ticket, priority));
                continue;
            }

            pendingByMob.Remove(ticket.MobId);
            if (needsExclusiveSharedRoute)
            {
                activeSharedRouteKeys.Add(request.SharedRouteKey);
                sharedRouteOwnerRequests.Add(request.RequestId);
            }
            RequeueDeferred();
            return request;
        }

        RequeueDeferred();
        return null;

        void RequeueDeferred()
        {
            if (deferred is null) return;
            foreach (var item in deferred)
                requestOrder.Enqueue(item.Ticket, item.Priority);
        }
    }

    private void ReleaseSharedRoute(PathRequest request)
    {
        if (request.SharedRouteKey == 0) return;
        int workersToWake;
        lock (requestGate)
        {
            if (!sharedRouteOwnerRequests.Remove(request.RequestId))
                return;
            activeSharedRouteKeys.Remove(request.SharedRouteKey);
            workersToWake = stopping
                ? 0
                : Math.Min(workers.Length, pendingByMob.Count);
        }
        if (workersToWake > 0)
            requestReady.Release(workersToWake);
    }

    private bool HasUsableSharedRoute(long key)
    {
        lock (sharedRouteGate)
            return sharedRoutes.TryGetValue(key, out var route)
                && route.Waypoints.Count >= 2
                && !route.Waypoints.Any(waypoint => waypoint.Action == NavAction.Dig)
                && NavPathTerrain.IsValid(terrain, route);
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
