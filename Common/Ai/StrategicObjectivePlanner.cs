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
/// Deterministic team-level flag allocation, by marginal value in tickets per second.
///
/// There is no priority table any more. Every (squad, flag) pair is worth what
/// <see cref="StrategicValue.Marginal"/> says, travel included, and the assignment is whichever
/// pairing is worth the most — repeatedly, so each commitment consumes the value it converted and
/// the next squad sees a flag that is already being handled as the cheap thing it now is.
/// Reinforcement, defence and opportunism all fall out of that rather than being ranked in advance.
/// </summary>
public static class StrategicObjectivePlanner
{
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

        // Greedy by marginal value: repeatedly take the (squad, flag) pair worth the most and commit
        // it, so a flag's marginal value is consumed as squads are assigned and the next one naturally
        // looks elsewhere. The old code sorted discrete priority CLASSES and broke ties on distance,
        // which is what let a second squad at a fight (800) outrank a first squad at free ground (600)
        // and made the whole force pile onto one objective.
        var available = uniqueSquads.ToList();
        var assignedPerFlag = uniqueFlags.ToDictionary(flag => flag.FlagId, _ => 0);
        var assignments = new List<StrategicAssignment>(uniqueSquads.Length);

        while (available.Count > 0)
        {
            float bestValue = float.NegativeInfinity;
            int bestSquad = -1;
            uint bestFlag = 0;

            for (int squadIndex = 0; squadIndex < available.Count; squadIndex++)
                foreach (var flag in uniqueFlags)
                {
                    float value = StrategicValue.Marginal(
                        team,
                        flag,
                        assignedPerFlag[flag.FlagId],
                        TravelSeconds(available[squadIndex], flag));

                    // Staying put is worth something. A switching margin in the SAME unit as the
                    // value, rather than the old 15 m distance nudge, which could not damp a
                    // priority-class flip and so let squads oscillate between two flags.
                    if (available[squadIndex].CurrentFlagId == flag.FlagId)
                        value += CommitmentBonus;

                    // Deterministic ordering: ties resolve by flag then squad id, never by
                    // enumeration order, so the same situation always produces the same plan.
                    if (value > bestValue
                        || value == bestValue
                        && (bestSquad < 0
                            || flag.FlagId < bestFlag
                            || flag.FlagId == bestFlag
                            && available[squadIndex].SquadId < available[bestSquad].SquadId))
                    {
                        bestValue = value;
                        bestSquad = squadIndex;
                        bestFlag = flag.FlagId;
                    }
                }

            if (bestSquad < 0) break;
            assignments.Add(new StrategicAssignment(available[bestSquad].SquadId, bestFlag));
            assignedPerFlag[bestFlag]++;
            available.RemoveAt(bestSquad);
        }

        return assignments
            .OrderBy(assignment => assignment.SquadId)
            .ToArray();
    }

    /// <summary>
    /// What a squad gives up by changing its mind, in tickets per second.
    ///
    /// Hysteresis has to be in the SAME unit as the value or it cannot hold against a change in it.
    /// The old bias was 15 metres applied to a distance that only broke ties between equal priority
    /// classes — so a flag flipping class reassigned the squad regardless, and the two flipped back
    /// and forth. A tenth of a flag is small enough that a genuinely better objective still wins.
    /// </summary>
    public const float CommitmentBonus = 0.1f;

    /// <summary>Rough seconds for this squad to reach this flag, at the movement solver's walk speed
    /// — the same time axis navigation prices routes in.</summary>
    private static float TravelSeconds(in StrategicSquad squad, in StrategicFlag flag)
    {
        float dx = squad.Home.X - flag.Position.X;
        float dz = squad.Home.Z - flag.Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz) / PlayerMovement.WalkSpeed;
    }
}
