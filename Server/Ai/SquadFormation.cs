using System.Numerics;

namespace Demiurge.GameServer;

internal readonly record struct SquadMember(
    ushort ActorId,
    int SquadIndex,
    Vector3 Position);

/// <summary>
/// Reassigns squad membership from live positions. Squads used to be fixed at spawn: the index came
/// from a monotonic per-team counter, <c>MobBrain.SquadIndex</c> was init-only, and the counter never
/// decremented on removal, so a squad could never re-form and a replacement NPC always became a squad
/// of one that the commander then sent to its own objective.
///
/// Pure so it can be tested headlessly, and deterministic: members are considered in actor-id order
/// and squad indices are the lowest free ones, so the same battlefield always produces the same
/// grouping regardless of dictionary iteration order.
/// </summary>
internal static class SquadFormation
{
    /// <summary>
    /// How far a member may drift from its squad centre before it is treated as separated.
    ///
    /// DERIVED from the manoeuvre the squad's own tactics can order, not chosen. It was a flat 30 m,
    /// and 30 m is smaller than the plan: <see cref="SquadTactics.ApproachPosition"/> sends a
    /// bounding man to <see cref="SquadTactics.OpeningStandoff"/> (45 m) from the threat on a
    /// bearing up to 90 degrees around it, so two men executing one squad's fire-and-movement are
    /// routinely further apart than the rule that says they are one squad. The squad therefore
    /// dissolved at the exact moment it was manoeuvring — measured on the conquest map, sixteen NPCs
    /// a side ended a fight as thirteen squads, nine of them a single man, and each fragment then
    /// drew its own flag from the commander. That is the "one or two units sent to capture a flag"
    /// report, and it is a second classifier disagreeing with the first rather than a tuning miss.
    ///
    /// One bound past the opening standoff, so a squad that has begun closing is still a squad.
    /// </summary>
    public const float CohesionRadius =
        SquadTactics.OpeningStandoff + SquadTactics.BoundLength;

    /// <summary>
    /// How close a separated member must be to another squad's centre to join it. The manoeuvre
    /// envelope itself, so it stays strictly smaller than <see cref="CohesionRadius"/> — joining has
    /// to be harder than staying or a man on the boundary oscillates between two squads, which is
    /// the churn this whole pass is about.
    /// </summary>
    public const float JoinRadius = SquadTactics.OpeningStandoff;

    /// <summary>
    /// Recomputes squad indices. Writes one entry per member into <paramref name="assignments"/>,
    /// which is cleared first. Members keep their squad whenever that is still reasonable, because
    /// role and bound state is keyed to the squad and thrashing membership would thrash the plan.
    /// </summary>
    public static void Plan(
        IReadOnlyList<SquadMember> members,
        Dictionary<ushort, int> assignments)
    {
        assignments.Clear();
        if (members.Count == 0) return;

        var ordered = new List<SquadMember>(members);
        ordered.Sort(static (left, right) => left.ActorId.CompareTo(right.ActorId));

        // Existing groups and their centres, from live positions rather than spawn points.
        var groups = new Dictionary<int, List<SquadMember>>();
        foreach (var member in ordered)
        {
            if (!groups.TryGetValue(member.SquadIndex, out var group))
                groups[member.SquadIndex] = group = [];
            group.Add(member);
        }

        var centres = new Dictionary<int, Vector3>();
        foreach (var pair in groups)
            centres[pair.Key] = Centre(pair.Value);

        // Keep whoever is still cohesive, nearest-first, up to capacity. Sorting by distance means an
        // over-strength squad sheds its outliers rather than whichever member happened to come first.
        var kept = new Dictionary<int, List<ushort>>();
        var separated = new List<SquadMember>();
        foreach (var pair in groups)
        {
            var candidates = new List<SquadMember>(pair.Value);
            Vector3 centre = centres[pair.Key];
            candidates.Sort((left, right) =>
            {
                int byDistance = HorizontalDistanceSquared(left.Position, centre)
                    .CompareTo(HorizontalDistanceSquared(right.Position, centre));
                return byDistance != 0 ? byDistance : left.ActorId.CompareTo(right.ActorId);
            });

            var group = new List<ushort>(SquadBlackboard.MaximumMembers);
            foreach (var member in candidates)
            {
                if (group.Count < SquadBlackboard.MaximumMembers
                    && HorizontalDistanceSquared(member.Position, centre)
                        <= CohesionRadius * CohesionRadius)
                {
                    group.Add(member.ActorId);
                    assignments[member.ActorId] = pair.Key;
                    continue;
                }
                separated.Add(member);
            }
            if (group.Count > 0) kept[pair.Key] = group;
        }

        // Separated members join the nearest squad with room, or start one.
        separated.Sort(static (left, right) => left.ActorId.CompareTo(right.ActorId));
        foreach (var member in separated)
        {
            int bestSquad = -1;
            float bestDistance = JoinRadius * JoinRadius;
            foreach (var pair in kept)
            {
                if (pair.Value.Count >= SquadBlackboard.MaximumMembers) continue;
                float distance = HorizontalDistanceSquared(
                    member.Position,
                    CentreOf(pair.Value, ordered));
                if (distance > bestDistance) continue;
                bestDistance = distance;
                bestSquad = pair.Key;
            }

            if (bestSquad < 0)
            {
                bestSquad = LowestFreeIndex(kept);
                kept[bestSquad] = [];
            }
            kept[bestSquad].Add(member.ActorId);
            assignments[member.ActorId] = bestSquad;
        }

        MergeUnderStrengthSquads(kept, ordered, assignments);
    }

    /// <summary>
    /// Folds under-strength squads into neighbours. Needed because the cohesion test above is measured
    /// against a squad's own centre, and a squad of one is trivially cohesive with itself: a lone unit
    /// standing beside a squad would otherwise keep its own index forever, which is exactly the
    /// "adjacent units with no squad should join nearby squads" case. Merging is one-directional and
    /// ordered so it cannot oscillate.
    /// </summary>
    private static void MergeUnderStrengthSquads(
        Dictionary<int, List<ushort>> squads,
        List<SquadMember> members,
        Dictionary<ushort, int> assignments)
    {
        for (int pass = 0; pass < SquadBlackboard.MaximumMembers; pass++)
        {
            int source = -1;
            int target = -1;
            foreach (var candidate in squads.OrderBy(pair => pair.Value.Count)
                         .ThenByDescending(pair => pair.Key))
            {
                if (candidate.Value.Count >= SquadBlackboard.MaximumMembers) continue;
                Vector3 candidateCentre = CentreOf(candidate.Value, members);

                foreach (var host in squads.OrderByDescending(pair => pair.Value.Count)
                             .ThenBy(pair => pair.Key))
                {
                    if (host.Key == candidate.Key
                        || host.Value.Count + candidate.Value.Count
                            > SquadBlackboard.MaximumMembers
                        || HorizontalDistanceSquared(
                               candidateCentre,
                               CentreOf(host.Value, members))
                           > JoinRadius * JoinRadius)
                        continue;
                    source = candidate.Key;
                    target = host.Key;
                    break;
                }
                if (source >= 0) break;
            }

            if (source < 0) return;
            foreach (ushort actorId in squads[source])
            {
                squads[target].Add(actorId);
                assignments[actorId] = target;
            }
            squads.Remove(source);
        }
    }

    private static int LowestFreeIndex(Dictionary<int, List<ushort>> squads)
    {
        for (int index = 0; ; index++)
            if (!squads.ContainsKey(index))
                return index;
    }

    private static Vector3 Centre(List<SquadMember> members)
    {
        Vector3 sum = Vector3.Zero;
        foreach (var member in members) sum += member.Position;
        return sum / members.Count;
    }

    private static Vector3 CentreOf(List<ushort> actorIds, List<SquadMember> members)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (var member in members)
        {
            if (!actorIds.Contains(member.ActorId)) continue;
            sum += member.Position;
            count++;
        }
        return count == 0 ? Vector3.Zero : sum / count;
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
