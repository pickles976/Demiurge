using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>Per-NPC state which is not part of the replicated player representation.</summary>
internal sealed class MobBrain
{
    public int Team { get; init; }
    public int SquadIndex { get; init; }
    public uint ObjectiveRevision { get; set; }
    public PathFollower Path { get; } = new();
    public ContactMemory Contacts { get; } = new();
    public int PerceptionCursor { get; set; }
    public ushort CombatTargetId { get; set; }
    public uint TargetAcquiredTick { get; set; }
    public Vector3 AimDirection { get; set; }
    public uint ShotSequence { get; set; }
    public int BurstShotsRemaining { get; set; }
    public uint NextBurstTick { get; set; }
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

    public void ClearCombatTarget()
    {
        CombatTargetId = 0;
        TargetAcquiredTick = 0;
        BurstShotsRemaining = 0;
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
        Path.Clear();
    }
}
