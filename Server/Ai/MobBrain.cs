using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>Per-NPC state which is not part of the replicated player representation.</summary>
internal sealed class MobBrain
{
    internal const int IncomingFireResponseTicks = 2 * NetworkConfig.TickRate;

    public int Team { get; init; }

    /// <summary>
    /// Settable, unlike before: squads re-form from live proximity every second, so an NPC can change
    /// squad mid-fight. See <see cref="SquadFormation"/>.
    /// </summary>
    public int SquadIndex { get; set; }

    /// <summary>
    /// Envelope side from the squad plan. Persisted rather than recomputed so it is sticky: a replan
    /// mid-manoeuvre must not send a committed flanker back across the threat axis.
    /// </summary>
    public FlankSide FlankSide { get; set; }

    /// <summary>
    /// How many bounds this member has completed. Each one shortens its standoff, which is what makes
    /// the squad close in rather than shuffle at a fixed range.
    /// </summary>
    public int BoundIndex { get; set; }

    /// <summary>
    /// In position and able to shoot. The squad plan will not order anyone to move unless at least one
    /// member is set, which is what keeps a bound covered by fire.
    /// </summary>
    public bool IsSet => AtCover;
    public uint ObjectiveRevision { get; set; }
    public NavigationAgent Navigation { get; } = new();
    public ContactMemory Contacts { get; } = new();
    public int PerceptionCursor { get; set; }

    /// <summary>
    /// Which body point perception last had a clear line to, and on whom. Combat aims here instead of
    /// assuming centre mass, so a target exposing only its head over cover is actually shot at rather
    /// than being fired into the dirt in front of it.
    /// </summary>
    public ushort PerceivedTargetId { get; set; }
    public float PerceivedAimHeight { get; set; } = GunConfig.PlayerCenterHeight;
    public ushort CombatTargetId { get; set; }
    public uint TargetAcquiredTick { get; set; }
    public Vector3 AimDirection { get; set; }
    public uint ShotSequence { get; set; }
    public int BurstShotsRemaining { get; set; }
    public uint NextBurstTick { get; set; }
    public uint NextPrecisionShotTick { get; set; }
    public uint NextSuppressionShotTick { get; set; }
    public uint NextGrenadeDecisionTick { get; set; }
    public bool HasCoverDestination { get; set; }
    public bool AtCover { get; set; }
    public Vector3 CoverDestination { get; set; }
    public Vector3 CoverPeekPosition { get; set; }
    public CoverKind CoverKind { get; set; }
    public ushort CoverThreatId { get; set; }
    public Vector3 CoverThreatPosition { get; set; }
    public long CoverTerrainVersion { get; set; }
    public uint NextCoverQueryTick { get; set; }
    public uint CoverArrivedTick { get; set; }
    public ushort HeardActorId { get; set; }
    public Vector3 HeardPosition { get; set; }
    public uint HeardTick { get; set; }
    public uint HeardRevision { get; set; }
    public uint AppliedHeardRevision { get; set; }
    public bool ShouldCloseDistance { get; set; }
    public uint UnderFireUntilTick { get; private set; }

    public void MarkUnderFire(uint tick)
        => UnderFireUntilTick = Math.Max(
            UnderFireUntilTick,
            tick + IncomingFireResponseTicks);

    public bool IsUnderFire(uint tick)
        => tick < UnderFireUntilTick;

    public void ClearUnderFire() => UnderFireUntilTick = 0;

    public void ClearCombatTarget()
    {
        CombatTargetId = 0;
        TargetAcquiredTick = 0;
        BurstShotsRemaining = 0;
        NextPrecisionShotTick = 0;
        ShouldCloseDistance = false;
    }

    public void ClearCover()
    {
        HasCoverDestination = false;
        AtCover = false;
        CoverDestination = default;
        CoverPeekPosition = default;
        CoverKind = CoverKind.None;
        CoverThreatId = 0;
        CoverThreatPosition = default;
        CoverTerrainVersion = 0;
        CoverArrivedTick = 0;
        Navigation.Path.Clear();
    }

    public bool HasRecentGunshot(uint tick)
        => HeardActorId != 0
           && tick >= HeardTick
           && tick - HeardTick < GunshotHearing.InvestigationTicks;

    public void ClearGunshot()
    {
        HeardActorId = 0;
        HeardPosition = default;
        HeardTick = 0;
        HeardRevision = 0;
        AppliedHeardRevision = 0;
    }
}
