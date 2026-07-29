using System.Collections.Concurrent;
using System.Diagnostics;

namespace Demiurge.GameServer;

/// <summary>
/// Owns the single navigation worker. The main server thread only submits immutable requests and
/// drains completed paths; it never waits for A*.
/// </summary>
internal sealed class NavigationSystem : IDisposable
{
    internal readonly record struct PathResult(
        ushort MobId,
        long RequestId,
        long TerrainVersion,
        NavPath Path,
        long ElapsedMicroseconds);

    internal readonly record struct Metrics(
        long Requested,
        long Completed,
        long SearchMicroseconds);

    private sealed record PathRequest(
        ushort MobId,
        long RequestId,
        NavCell Start,
        INavGoal Goal,
        bool AllowJump);

    private readonly ChunkMap terrain;
    private readonly object requestGate = new();
    private readonly Queue<ushort> requestOrder = new();
    private readonly Dictionary<ushort, PathRequest> pendingByMob = new();
    private readonly ConcurrentQueue<PathResult> completed = new();
    private readonly AutoResetEvent requestReady = new(false);
    private readonly Thread worker;

    private bool stopping;
    private long nextRequestId;
    private long requestedCount;
    private long completedCount;
    private long searchMicroseconds;

    public NavigationSystem(ChunkMap terrain)
    {
        this.terrain = terrain;
        worker = new Thread(Work)
        {
            IsBackground = true,
            Name = "Demiurge navigation",
        };
        worker.Start();
    }

    /// <summary>
    /// Enqueues one current request per mob. If that mob is still waiting in the FIFO, this request
    /// replaces it in-place instead of growing the queue.
    /// </summary>
    public long Request(
        ushort mobId,
        NavCell start,
        INavGoal goal,
        bool allowJump = true)
    {
        long requestId = Interlocked.Increment(ref nextRequestId);
        lock (requestGate)
        {
            if (stopping) return 0;
            if (!pendingByMob.ContainsKey(mobId))
                requestOrder.Enqueue(mobId);
            pendingByMob[mobId] = new PathRequest(
                mobId,
                requestId,
                start,
                goal,
                allowJump);
        }

        Interlocked.Increment(ref requestedCount);
        requestReady.Set();
        return requestId;
    }

    public bool TryGetCompleted(out PathResult result)
        => completed.TryDequeue(out result);

    public Metrics SnapshotMetrics()
        => new(
            Interlocked.Read(ref requestedCount),
            Interlocked.Read(ref completedCount),
            Interlocked.Read(ref searchMicroseconds));

    public void Dispose()
    {
        lock (requestGate)
            stopping = true;
        requestReady.Set();
        worker.Join();
        requestReady.Dispose();
    }

    private void Work()
    {
        while (true)
        {
            PathRequest? request;
            lock (requestGate)
            {
                if (stopping) return;
                request = TakeRequest();
            }

            if (request is null)
            {
                requestReady.WaitOne();
                continue;
            }

            long terrainVersion = terrain.EditVersion;
            long started = Stopwatch.GetTimestamp();
            NavPath path = NavSearch.Find(
                terrain,
                request.Start,
                request.Goal,
                NavSearchOptions.Default with { AllowJump = request.AllowJump });
            long elapsedUs = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000d);

            completed.Enqueue(new PathResult(
                request.MobId,
                request.RequestId,
                terrainVersion,
                path,
                elapsedUs));
            Interlocked.Increment(ref completedCount);
            Interlocked.Add(ref searchMicroseconds, elapsedUs);
        }
    }

    private PathRequest? TakeRequest()
    {
        while (requestOrder.Count > 0)
        {
            ushort mobId = requestOrder.Dequeue();
            if (pendingByMob.Remove(mobId, out var request))
                return request;
        }
        return null;
    }
}
