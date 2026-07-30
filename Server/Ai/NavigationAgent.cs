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

    private PendingRequest? pending;
    private long? blockedCellKey;
    private int blockedReplansRemaining;

    public PathFollower Path { get; } = new();
    public NavigationProgressWatch Progress { get; } = new();
    public Vector3 Destination { get; private set; }
    public bool HasDestination { get; private set; }

    public void SetDestination(Vector3 destination, bool clearPath = true)
    {
        Destination = destination;
        HasDestination = true;
        if (clearPath)
            Path.Clear();
    }

    public bool HasPending(bool forCover)
        => pending is { } request && request.ForCover == forCover;

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

    public void ResetBlocked()
    {
        blockedCellKey = null;
        blockedReplansRemaining = 0;
    }

    public void RememberBlocked(NavCell? blockedCell)
    {
        if (blockedCell is not { } cell) return;
        blockedCellKey = cell.Key;
        blockedReplansRemaining = 3;
    }

    public long? TakeAvoidedCell()
    {
        if (blockedCellKey is not { } key || blockedReplansRemaining <= 0)
            return null;
        blockedReplansRemaining--;
        return key;
    }

    public void Clear()
    {
        Path.Clear();
        Progress.Reset();
        pending = null;
        ResetBlocked();
        Destination = default;
        HasDestination = false;
    }
}
