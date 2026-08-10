using System.Collections.Concurrent;
using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Solves long-range flow fields off the main thread and publishes them as they finish.
///
/// Nothing waits for this. A field is an OPTIMISATION over the local search, not a prerequisite for
/// it, so the server starts, spawns, and plays exactly as it does today while the solve runs; callers
/// ask <see cref="TryGet"/> and fall back to A* whenever the answer is no. That is what keeps a
/// several-second build off the iteration cycle — hitting F5 does not wait for navigation to think.
///
/// One thread, not the search pool. The pool exists to answer live requests with a deadline; a field
/// is a long batch job that would sit at the head of that queue and starve them. It also means the
/// solve holds exactly one core, which matters on a six-core reference machine already sharing DDR4
/// with the renderer.
///
/// Terrain edits are NOT yet repaired — a published field records the revision it was solved against
/// (<see cref="NavFlowField.TerrainVersion"/>) and callers decide what staleness they will accept.
/// Incremental repair from a dirty frontier is the intended follow-up.
/// </summary>
internal sealed class NavFlowFieldService : IDisposable
{
    private readonly ChunkMap terrain;
    private readonly ConcurrentDictionary<long, NavFlowField> fields = new();
    private readonly Thread worker;
    private readonly BlockingCollection<NavCell> queue = new();
    private volatile bool stopping;

    public NavFlowFieldService(ChunkMap terrain)
    {
        this.terrain = terrain;
        worker = new Thread(Work)
        {
            IsBackground = true,
            Name = "Demiurge flow fields",
            // Below the search pool and the tick. A field that arrives a second later costs nothing;
            // a tick that arrives a millisecond later is a dropped frame's worth of budget.
            Priority = ThreadPriority.BelowNormal,
        };
        worker.Start();
    }

    /// <summary>Fields solved so far. Grows as the queue drains.</summary>
    public int Ready => fields.Count;

    public int Pending => queue.Count;

    /// <summary>
    /// Queues a destination. Ordering is the order requested, so a caller that wants the contested
    /// centre before the far corner should ask for it first.
    /// </summary>
    public void Request(NavCell destination)
    {
        if (stopping || fields.ContainsKey(destination.Key)) return;
        queue.Add(destination);
    }

    /// <summary>
    /// The field for a destination, if it has been solved. False is the ordinary answer while the
    /// solve is still running and the caller should use the local search.
    /// </summary>
    public bool TryGet(NavCell destination, out NavFlowField field)
        => fields.TryGetValue(destination.Key, out field!);

    /// <summary>
    /// The solved field whose destination is nearest <paramref name="position"/> and which has a
    /// route from <paramref name="from"/>. Fields are few — one per objective — so a linear scan is
    /// cheaper than any index, and it runs once per replan rather than per tick.
    /// </summary>
    public bool TryGetFor(Vector3 position, NavCell from, out NavFlowField field)
    {
        field = null!;
        float best = float.MaxValue;
        foreach (var candidate in fields.Values)
        {
            if (candidate.CostFrom(from) is null) continue;
            var destination = candidate.Destination;
            float dx = destination.X + 0.5f - position.X;
            float dz = destination.Z + 0.5f - position.Z;
            float distance = dx * dx + dz * dz;
            if (distance >= best) continue;
            best = distance;
            field = candidate;
        }
        return field is not null;
    }

    private void Work()
    {
        try
        {
            foreach (var destination in queue.GetConsumingEnumerable())
            {
                if (stopping) return;
                var field = NavFlowField.Build(
                    terrain,
                    destination,
                    cancellationRequested: () => stopping);
                if (stopping) return;
                if (field.Count > 0)
                    fields[destination.Key] = field;
            }
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced the consumer during shutdown; nothing to publish.
        }
    }

    public void Dispose()
    {
        stopping = true;
        queue.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(5));
        queue.Dispose();
    }
}
