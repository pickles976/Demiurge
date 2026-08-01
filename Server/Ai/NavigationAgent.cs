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

    /// <summary>
    /// How long an excavation stays the committed site after the last bite landed. Long enough to
    /// survive the replan each bite forces, short enough that abandoning a cut is still possible when
    /// the situation changes.
    /// </summary>
    private const uint DigSiteMemoryTicks = 15 * NetworkConfig.TickRate;

    private PendingRequest? pending;
    private long? blockedCellKey;
    private int blockedReplansRemaining;
    private NavCell? digSite;
    private uint digSiteTick;
    private (int X, int Z)? escapeDirection;
    private float escapeGrade;
    private Vector2? escapeTread;

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

    public bool TryGetEscape(out int dx, out int dz, out float grade)
    {
        if (escapeDirection is not { } direction)
        {
            dx = dz = 0;
            grade = 0f;
            return false;
        }
        dx = direction.X;
        dz = direction.Z;
        grade = escapeGrade;
        return true;
    }

    public void StartEscape(int dx, int dz, float grade)
    {
        escapeDirection = (dx, dz);
        escapeGrade = grade;
        Path.Clear();
    }

    public void SetEscapeTread(NavCell? cell)
        => escapeTread = cell is { } tread ? tread.CentreXZ : null;

    public bool TryGetEscapeTread(out Vector2 tread)
    {
        tread = escapeTread.GetValueOrDefault();
        return escapeTread.HasValue;
    }

    public void StopEscape()
    {
        escapeDirection = null;
        escapeGrade = 0f;
        escapeTread = null;
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
        ForgetDigSite();
        StopEscape();
        Destination = default;
        HasDestination = false;
    }
}
