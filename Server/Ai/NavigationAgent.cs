using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Owns the complete per-NPC navigation lifecycle. Decision code supplies destinations; this object
/// owns request generations, the installed path, partial prefetch state, blocked-cell avoidance,
/// and long-term progress detection.
/// </summary>
internal sealed class NavigationAgent
{
    private readonly record struct PendingRequest(long RequestId, bool ForCover);

    // Short bounded results can arrive already inside PathFollower's 12 m refresh distance. Without
    // a cadence, installing one immediately queues its successor and makes worker throughput—not
    // actor movement—the replan rate. Half a second still starts the next segment several seconds
    // before an ordinary 12 m walk prefix is consumed, while capping speculative churn at 2 Hz.
    private const uint PrefetchCadenceTicks = NetworkConfig.TickRate / 2;

    /// <summary>
    /// How long an excavation stays the committed site after the last bite landed. Long enough to
    /// survive the replan each bite forces, short enough that abandoning a cut is still possible when
    /// the situation changes.
    /// </summary>
    private const uint DigSiteMemoryTicks = 15 * NetworkConfig.TickRate;
    private const int MaximumPartialBacktrackCells = 64;

    private PendingRequest? pending;
    private long? blockedCellKey;
    private readonly Queue<long> partialBacktrackCells = new();
    private readonly HashSet<long> partialBacktrackCellSet = [];
    private int partialBacktrackAttempts;
    private NavCell? digSite;
    private uint digSiteTick;
    private uint nextPrefetchTick;

    public PathFollower Path { get; } = new();
    public NavigationProgressWatch Progress { get; } = new();
    public Vector3 Destination { get; private set; }
    public bool HasDestination { get; private set; }

    /// <summary>
    /// This actor's planned cut is a ROUTE corridor, so its bites use the narrow 1 m-lattice brush.
    ///
    /// It is a destination heuristic and it is not a good one — see AI_TODO section 4. The intent it
    /// is guessing at ("am I cutting a staircase, or widening clearance?") belongs to the PLAN, and
    /// the plan does not currently carry it. Deleting the guess and giving every planned cut the
    /// narrow brush was tried on 2026-08-12 and regresses two scenarios outright, because the same
    /// call site serves both intents: a 1 m tube is right for a stair tread and leaves an actor
    /// without standing clearance under a low tunnel mouth or across an unwalkable slope face.
    /// </summary>
    public bool PreciseExcavation { get; private set; }

    public void SetDestination(Vector3 destination, bool clearPath = true)
    {
        Destination = destination;
        HasDestination = true;
        PreciseExcavation = false;
        if (clearPath)
            Path.Clear();
    }

    public void CommitPreciseExcavation() => PreciseExcavation = true;

    public bool HasPending(bool forCover)
        => pending is { } request && request.ForCover == forCover;

    public bool HasAnyPending => pending is not null;

    public bool CanPrefetch(uint tick) => tick >= nextPrefetchTick;

    public void RecordPrefetch(uint tick)
        => nextPrefetchTick = tick + PrefetchCadenceTicks;

    public void RecordRequest(long requestId, bool forCover)
        => pending = new PendingRequest(requestId, forCover);

    public bool TryCompleteRequest(long requestId, out bool forCover)
    {
        if (pending is not { } request || request.RequestId != requestId)
        {
            forCover = false;
            return false;
        }
        pending = null;
        forCover = request.ForCover;
        return true;
    }

    public long? CancelPending(bool? forCover = null)
    {
        if (pending is { } request
            && (forCover is null || request.ForCover == forCover.Value))
        {
            pending = null;
            return request.RequestId;
        }
        return null;
    }

    /// <summary>
    /// Records where a shovel bite just landed, so the next search prefers finishing this cut over
    /// opening a fresh one. Every bite changes the terrain and therefore forces a replan, and without
    /// this the replan re-picked its best frontier from scratch and the NPC wandered off mid-excavation.
    /// </summary>
    public void RememberDigSite(Vector3 target, uint tick)
    {
        digSite = new NavCell(
            (int)MathF.Floor(target.X),
            (int)MathF.Floor(target.Y),
            (int)MathF.Floor(target.Z));
        digSiteTick = tick;
    }

    public NavCell? PreferredDigSite(uint tick)
        => digSite is { } site && tick - digSiteTick <= DigSiteMemoryTicks ? site : null;

    public void ForgetDigSite() => digSite = null;

    public void ResetBlocked()
    {
        blockedCellKey = null;
    }

    public void RememberBlocked(NavCell? blockedCell)
    {
        if (blockedCell is not { } cell) return;
        blockedCellKey = cell.Key;
    }

    public long? TakeAvoidedCell()
        => blockedCellKey;

    public void RememberPartialBacktrack(NavCell? cell)
    {
        if (cell is not { } value) return;
        partialBacktrackAttempts++;
        if (!partialBacktrackCellSet.Add(value.Key)) return;
        partialBacktrackCells.Enqueue(value.Key);
        while (partialBacktrackCells.Count > MaximumPartialBacktrackCells)
            partialBacktrackCellSet.Remove(partialBacktrackCells.Dequeue());
    }

    public IReadOnlyList<long> PartialBacktrackCellKeys
        => partialBacktrackCells.ToArray();

    public int PartialBacktrackAttempts => partialBacktrackAttempts;
    public bool HasBegunPartialRecovery => partialBacktrackAttempts > 0;

    /// <summary>
    /// Several consumed bounded prefixes without escape means this actor is resolving a local
    /// navigation basin. Re-forming it into another squad every second changes the goal and cancels
    /// the deeper recovery search before a worker can finish it.
    /// </summary>
    public bool IsRecoveringFromPartialTrap => partialBacktrackAttempts >= 4;

    public void Clear(bool preservePartialBacktrack = false)
    {
        Path.Clear();
        Progress.Reset();
        pending = null;
        ResetBlocked();
        if (!preservePartialBacktrack)
            ClearPartialBacktrack();
        ForgetDigSite();
        nextPrefetchTick = 0;
        Destination = default;
        HasDestination = false;
        PreciseExcavation = false;
    }

    public void ClearPartialBacktrack()
    {
        partialBacktrackCells.Clear();
        partialBacktrackCellSet.Clear();
        partialBacktrackAttempts = 0;
    }
}
