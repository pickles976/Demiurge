using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>Per-NPC state which is not part of the replicated player representation.</summary>
internal sealed class MobBrain
{
    public PathFollower Path { get; } = new();
    public ContactMemory Contacts { get; } = new();
    public int PerceptionCursor { get; set; }
    public ushort CombatTargetId { get; set; }
    public uint TargetAcquiredTick { get; set; }
    public Vector3 AimDirection { get; set; }
    public uint ShotSequence { get; set; }
    public int BurstShotsRemaining { get; set; }
    public uint NextBurstTick { get; set; }

    public void ClearCombatTarget()
    {
        CombatTargetId = 0;
        TargetAcquiredTick = 0;
        BurstShotsRemaining = 0;
    }
}
