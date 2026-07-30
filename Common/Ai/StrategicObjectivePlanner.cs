using System.Numerics;

namespace Demiurge;

/// <summary>A team-relative view of one capturable flag for strategic planning.</summary>
public readonly record struct StrategicFlag(
    uint FlagId,
    Vector3 Position,
    int OwnerTeam,
    int CapturingTeam,
    float Progress,
    int FriendlyPresence,
    int EnemyPresence);

/// <summary>Stable squad identity and its long-term origin/current assignment.</summary>
public readonly record struct StrategicSquad(
    int SquadId,
    Vector3 Home,
    uint CurrentFlagId = 0);

public readonly record struct StrategicAssignment(int SquadId, uint FlagId);

/// <summary>
/// Deterministic team-level flag allocation. High-value objectives create reinforcement slots, so
/// a threatened friendly point can pull a second squad before a quiet rear flag receives one.
/// Travel only decides which squad fills a selected slot; it cannot make a nearby low-value flag
/// outrank an active defensive emergency.
/// </summary>
public static class StrategicObjectivePlanner
{
    public const float ReassignmentBiasMetres = 15f;

    private readonly record struct ObjectiveSlot(
        StrategicFlag Flag,
        int Ordinal,
        int Priority);

    public static IReadOnlyList<StrategicAssignment> Plan(
        int team,
        IReadOnlyList<StrategicSquad> squads,
        IReadOnlyList<StrategicFlag> flags)
    {
        if (team <= 0 || squads.Count == 0 || flags.Count == 0)
            return [];

        var uniqueSquads = squads
            .GroupBy(squad => squad.SquadId)
            .Select(group => group.First())
            .OrderBy(squad => squad.SquadId)
            .ToArray();
        var uniqueFlags = flags
            .Where(flag => flag.FlagId != 0)
            .GroupBy(flag => flag.FlagId)
            .Select(group => group.First())
            .OrderBy(flag => flag.FlagId)
            .ToArray();
        if (uniqueFlags.Length == 0) return [];

        var slots = new List<ObjectiveSlot>(uniqueFlags.Length * uniqueSquads.Length);
        foreach (var flag in uniqueFlags)
            for (int ordinal = 0; ordinal < uniqueSquads.Length; ordinal++)
                slots.Add(new ObjectiveSlot(
                    flag,
                    ordinal,
                    SlotPriority(team, flag, ordinal)));

        var available = uniqueSquads.ToList();
        var pendingSlots = slots
            .OrderByDescending(slot => slot.Priority)
            .ThenBy(slot => slot.Ordinal)
            .ThenBy(slot => slot.Flag.FlagId)
            .ToList();
        var assignments = new List<StrategicAssignment>(uniqueSquads.Length);
        while (available.Count > 0 && pendingSlots.Count > 0)
        {
            int priority = pendingSlots[0].Priority;
            int bestSlotIndex = 0;
            int bestSquadIndex = 0;
            float bestDistance = float.PositiveInfinity;
            for (int slotIndex = 0;
                 slotIndex < pendingSlots.Count
                 && pendingSlots[slotIndex].Priority == priority;
                 slotIndex++)
            {
                var candidateSlot = pendingSlots[slotIndex];
                for (int squadIndex = 0; squadIndex < available.Count; squadIndex++)
                {
                    float distance = EffectiveDistance(
                        available[squadIndex],
                        candidateSlot.Flag);
                    var bestSlot = pendingSlots[bestSlotIndex];
                    if (distance < bestDistance
                        || distance == bestDistance
                        && (candidateSlot.Flag.FlagId < bestSlot.Flag.FlagId
                            || candidateSlot.Flag.FlagId == bestSlot.Flag.FlagId
                            && available[squadIndex].SquadId
                                < available[bestSquadIndex].SquadId))
                    {
                        bestSlotIndex = slotIndex;
                        bestSquadIndex = squadIndex;
                        bestDistance = distance;
                    }
                }
            }

            var slot = pendingSlots[bestSlotIndex];
            assignments.Add(new StrategicAssignment(
                available[bestSquadIndex].SquadId,
                slot.Flag.FlagId));
            available.RemoveAt(bestSquadIndex);
            pendingSlots.RemoveAt(bestSlotIndex);
        }

        return assignments
            .OrderBy(assignment => assignment.SquadId)
            .ToArray();
    }

    private static int SlotPriority(int team, StrategicFlag flag, int ordinal)
    {
        bool friendlyOwned = flag.OwnerTeam == team;
        bool neutral = flag.OwnerTeam == FlagConfig.NeutralTeam;
        bool enemyCapture =
            flag.CapturingTeam != FlagConfig.NeutralTeam
            && flag.CapturingTeam != team;
        bool friendlyCapture = flag.CapturingTeam == team;
        bool contested = flag.FriendlyPresence > 0 && flag.EnemyPresence > 0;
        bool threatenedFriendly =
            friendlyOwned
            && (flag.Progress < 0.999f || enemyCapture || flag.EnemyPresence > 0);

        if (ordinal == 0)
        {
            if (threatenedFriendly) return 1_000;
            if (contested) return 950;
            if (!friendlyOwned && flag.FriendlyPresence > 0) return 900;
            if (neutral && friendlyCapture) return 850;
            if (neutral && (enemyCapture || flag.EnemyPresence > 0)) return 800;
            if (neutral) return 600;
            if (!friendlyOwned) return 550;
            return 250; // Quiet friendly rear-area security.
        }

        if (ordinal == 1)
        {
            if (threatenedFriendly) return 850;
            if (contested) return 800;
            if (!friendlyOwned && flag.FriendlyPresence > 0) return 750;
            if (neutral && (friendlyCapture || enemyCapture)) return 700;
            if (neutral || !friendlyOwned) return 300;
            return 100;
        }

        // More than two squads on one capture radius has sharply diminishing strategic value.
        // These low reserve slots only matter when there are more squads than useful objectives.
        return Math.Max(0, 180 - (ordinal - 2) * 40);
    }

    private static float EffectiveDistance(StrategicSquad squad, StrategicFlag flag)
    {
        float dx = squad.Home.X - flag.Position.X;
        float dz = squad.Home.Z - flag.Position.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (squad.CurrentFlagId == flag.FlagId)
            distance = MathF.Max(0f, distance - ReassignmentBiasMetres);
        return distance;
    }
}
