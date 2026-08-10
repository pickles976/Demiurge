using System.Numerics;

namespace Demiurge.GameServer;

internal enum SquadRole : byte
{
    /// <summary>No believed threat. The member follows its objective normally.</summary>
    None,

    /// <summary>Hold and shoot. Its value is mostly the damage it PREVENTS to whoever is moving.</summary>
    BaseOfFire,

    /// <summary>Move to a new bearing while the rest of the squad shoots.</summary>
    Bound,
}

/// <summary>One member, as the allocation needs to see him.</summary>
/// <param name="ExposureHere">Fraction of his silhouette the threat can reach where he stands.</param>
/// <param name="MovingSinceTick">When his current bound began, or 0 if he is not moving. A mover who
/// cannot arrive must not hold the rotation shut.</param>
internal readonly record struct SquadMemberState(
    ushort ActorId,
    Vector3 Position,
    ItemType Weapon,
    SelfExposure ExposureHere,
    float SkillFactor,
    int BoundIndex,
    uint MovingSinceTick,
    /// <summary>Bearing this man is already committed to. Kept across replans: the allocation runs at
    /// 2 Hz, so a bearing chosen fresh each time redirects a mover twice a second and he covers no
    /// ground.</summary>
    Commitment<float> CommittedBearing = default);

internal readonly record struct SquadPlanInput(
    Vector3 Threat,
    bool HasThreat,
    ItemType ThreatWeapon,
    float Aggression,
    uint Tick);

/// <summary>
/// What a member has been told to do. <paramref name="Bearing"/> is the approach angle in radians
/// around the threat, so two movers on different bearings cannot be stopped by the same cover.
/// </summary>
internal readonly record struct SquadTacticalOrder(
    ushort ActorId,
    SquadRole Role,
    Vector3 Destination,
    float Bearing,
    int BoundIndex);

/// <summary>
/// Who does which, priced in the same currency as everything else.
///
/// This is an ALLOCATION, not a doctrine. It does not decide that squads bound, or that riflemen
/// hold and SMGs close; it computes what each member is worth in each role and picks the assignment
/// with the highest squad total. Those behaviours then appear because the numbers say so.
///
/// The reason it must be joint rather than per-member: a base of fire's value is mostly the damage
/// it PREVENTS to a mover, by inflating the threat's dispersion (BallisticsConfig.SuppressedMoa).
/// Scored individually, suppressing an enemy you cannot reliably hit looks worthless, every man
/// independently concludes that moving is dangerous, and the whole squad stands still — which is
/// precisely the behaviour this replaces.
///
/// There is deliberately NO precondition on movement. The previous version refused to issue a bound
/// unless somebody was already at cover, so a squad that could not reach cover was forbidden from
/// moving and stood in the open digging. The allocation here always returns an assignment; its worst
/// case is that everybody shoots.
///
/// Pure and deterministic, so the doctrine can be tested headlessly. Terrain is absent by design:
/// this produces intent, and navigation and cover selection resolve it against the real field.
/// </summary>
internal static class SquadTactics
{
    /// <summary>Standoff for the first bound, and how much closer each one gets.</summary>
    public const float OpeningStandoff = 45f;
    public const float BoundLength = 12f;
    /// <summary>
    /// Closest a plan will deliberately place a man, whatever his weapon prefers. Below this the
    /// movement solver and the cover query fight over the same metre of ground.
    ///
    /// It used to be the standoff itself — one number for every weapon — which priced a bolt gun's
    /// assault at a range it does not want and a submachine gun's at one it has already won at.
    /// Where a man actually wants to be now comes from his own damage curve, via
    /// <see cref="WeaponEffectiveness.PreferredRange"/>; this is only the floor under it.
    /// </summary>
    public const float ClosestPlannedStandoff = 4f;

    /// <summary>
    /// Bearings a mover may approach on, in radians either side of the threat axis. Wide enough that
    /// one piece of cover cannot defeat two of them, which is the only reason to spread at all.
    /// </summary>
    private static readonly float[] Bearings =
        [-1.05f, 1.05f, -0.52f, 0.52f, -1.57f, 1.57f];

    /// <summary>
    /// How long a man may be moving before the rotation stops waiting for him.
    ///
    /// Self-clocking rotation — the next man goes when the last one arrives — deadlocks whenever a
    /// mover cannot arrive, and "cannot arrive" is common: pinned, blocked, or sent somewhere that
    /// turned out unreachable. This is the escape hatch, and it is why the ungated allocation above
    /// is not enough on its own.
    /// </summary>
    public const uint MoverTimeoutTicks = 5 * NetworkConfig.TickRate;

    /// <summary>How long a mover holds the bearing it was given. Matched to the timeout, so a bearing
    /// cannot outlive the bound that justified it.</summary>
    public const uint BearingCommitmentTicks = MoverTimeoutTicks;

    /// <summary>
    /// Men who must stay on the gun while the rest move. One — and the constraint really is only
    /// one, because everything else about who moves is already decided by the score.
    ///
    /// There used to be a flat `MaxMovers = 2` here, which quietly overrode the allocation: four of a
    /// six-man squad were held static regardless of what they were worth, so the squad's average
    /// advance was bounded by a constant rather than by the fight. Measured against a lone rifleman,
    /// lifting the cap took a thirty-second assault from 5.1 m of closing to 13.8 m — and DROPPED
    /// excavation from 50 bites to 24, because men who are moving are not digging.
    /// </summary>
    private const int SuppressorsRequired = 1;

    /// <summary>
    /// How much better the destination must be, in net health per second, before a man gives up his
    /// firing position for it.
    ///
    /// Leaving the line costs things the score does not model: seconds in transit dealing nothing,
    /// a settled aim thrown away, a position already known to work. So a marginal improvement is not
    /// enough. Without this bar, a bolt gun in a mirror match scored a gain of about 0.1 HP/s from
    /// closing and duly charged — the exact opposite of the range doctrine, and invisible until
    /// squads were allowed more than two movers.
    /// </summary>
    private const float MinimumGainToLeaveTheFiringLine = 1f;

    /// <summary>
    /// How much of a sprinting man a shooter can actually reach, against a static one.
    ///
    /// Crossing open ground has two opposed effects and they very nearly cancel, which is the whole
    /// reason a bound is a tactic rather than suicide. A mover is the OBVIOUS target — his targeting
    /// likelihood goes to 1 rather than being shared one-over-squad with everyone still in the
    /// firing line — but he is also a MOVING target, and a shooter's aim lags a runner.
    ///
    /// Pricing only the first makes every assault look fatal; pricing only the second makes every
    /// assault look free, which is what the model did before this and why closing was systematically
    /// underpriced.
    /// </summary>
    private static readonly SelfExposure SprintingExposure = SelfExposure.Of(0.45f);

    public static void Plan(
        in SquadPlanInput input,
        IReadOnlyList<SquadMemberState> members,
        List<SquadTacticalOrder> orders)
    {
        orders.Clear();
        if (members.Count == 0) return;

        if (!input.HasThreat)
        {
            foreach (var member in members)
                orders.Add(new SquadTacticalOrder(
                    member.ActorId, SquadRole.None, Vector3.Zero, 0f, member.BoundIndex));
            return;
        }

        Span<float> holdValue = stackalloc float[members.Count];
        Span<float> moveValue = stackalloc float[members.Count];
        Span<bool> timedOut = stackalloc bool[members.Count];

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            float range = Horizontal(member.Position, input.Threat);

            // What he is worth standing where he is.
            holdValue[i] = CombatValue.Score(
                new Combatant(member.Weapon, 0f, member.SkillFactor),
                [Against(input, range, member.ExposureHere, members.Count)],
                input.Aggression);

            // What he would be worth if the squad closed ALL THE WAY, not one bound closer.
            //
            // One-step lookahead undervalues committed manoeuvre and does so worst for exactly the
            // men who should be manoeuvring: an SMG at 60 m correctly computes that being at 48 m is
            // still useless, so it never takes the first bound and never reaches the range where it
            // wins. The bound is the STEP; the assault is what is being priced.
            // The threat is SUPPRESSED in this estimate, because the squad will be shooting at it
            // while this man crosses — that is what a base of fire is for, and it is the only reason
            // moving in the open prices out at all.
            //
            // It belongs here rather than as a flat bonus added to every candidate's gain. Added
            // flat it was a constant, so it lifted every man above the bar equally and stopped
            // discriminating: a bolt gun in a mirror match would leave the firing line to charge,
            // which is the opposite of the range doctrine.
            var self = new Combatant(member.Weapon, 0f, member.SkillFactor);

            // Where THIS man's weapon is worth the most, not one number for the squad. The assault
            // being priced at a fixed 12 m is why a submachine gun would not commit: it correctly
            // computed that 12 m was no better than where it stood, when what it wanted was closer.
            float standoff = MathF.Max(
                ClosestPlannedStandoff,
                WeaponEffectiveness.PreferredRange(member.Weapon, member.SkillFactor));

            float atDestination = CombatValue.Score(
                self,
                [Against(input, standoff, SelfExposure.Full, members.Count,
                    BallisticsConfig.SuppressedMoa)],
                input.Aggression);

            // The crossing itself, priced at the midpoint: sole target, but a running one.
            float inTransit = CombatValue.Score(
                self,
                [new Engagement(
                    (range + standoff) * 0.5f,
                    input.ThreatWeapon,
                    BallisticsConfig.SuppressedMoa,
                    TargetExposure.Full,
                    SprintingExposure,
                    TheirTargetingLikelihood: 1f)],
                input.Aggression);

            moveValue[i] = 0.5f * (atDestination + inTransit);

            timedOut[i] = member.MovingSinceTick != 0
                && input.Tick >= member.MovingSinceTick
                && input.Tick - member.MovingSinceTick >= MoverTimeoutTicks;
        }

        // Rank by how much each man gains from moving, net of what the squad loses in fire. A man
        // already close and shooting well has little to gain; the man furthest back has most.
        var candidates = new List<(int Index, float Gain)>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            if (timedOut[i]) continue;
            candidates.Add((i, moveValue[i] - holdValue[i]));
        }

        // Copied out of the `in` parameter: a readonly-ref cannot be captured by the comparator.
        Vector3 threatPosition = input.Threat;
        candidates.Sort((left, right) =>
        {
            int byGain = right.Gain.CompareTo(left.Gain);
            if (byGain != 0) return byGain;
            // Deterministic tie-break: the man furthest from the threat moves first, then by id.
            float leftRange = Horizontal(members[left.Index].Position, threatPosition);
            float rightRange = Horizontal(members[right.Index].Position, threatPosition);
            int byRange = rightRange.CompareTo(leftRange);
            return byRange != 0
                ? byRange
                : members[left.Index].ActorId.CompareTo(members[right.Index].ActorId);
        });

        // A lone man has nobody to cover him, so moving in the open is pure loss. Anything larger
        // keeps at least one gun on the threat.
        // Everyone the score says should move, may move — save the men needed to keep the threat's
        // head down. A lone man has nobody to cover him, so moving in the open is pure loss.
        int allowedMovers = Math.Max(0, members.Count - SuppressorsRequired);

        // Bearings are STICKY. They used to be dealt out in candidate-sort order every replan, and
        // that order changes as men move — so a mover was reassigned to a different side of the
        // threat twice a second and spent the fight being redirected. Measured, 37 bounds produced
        // 13 m of displacement per man against a 4 m/s walk speed.
        //
        // A man who has committed to a bearing keeps it until he stops moving; only the men without
        // one draw from the pool, and they take bearings nobody is already using.
        var movers = new Dictionary<int, float>(allowedMovers);
        var taken = new HashSet<float>();

        foreach (var (index, _) in candidates)
        {
            if (movers.Count >= allowedMovers) break;

            // A LIVE commitment is not re-litigated against the number that produced it.
            //
            // The gain test used to be here too, and it undid the commitment it was standing next
            // to: gain is a function of live positions on both sides, so it dips below the bar for
            // half a second whenever the man crosses a fold or the threat shifts. That released him
            // mid-bound, which released his bearing (PlanSquadTactics calls Released on any
            // non-Bound order), so he stopped where he was; the next replan found the gain healthy
            // again and dealt him a FRESH bearing, usually a different one. Twice a second, that is
            // a man turning around rather than crossing.
            //
            // The bar belongs on STARTING a bound — it prices leaving a working firing position —
            // and the loop below is where it is applied. Continuing is bounded instead by
            // MoverTimeoutTicks, which already removed timed-out movers from `candidates`.
            if (!members[index].CommittedBearing.TryGet(input.Tick, out float committed)) continue;
            if (!taken.Add(committed)) continue;
            movers[index] = committed;
        }

        foreach (var (index, gain) in candidates)
        {
            if (movers.Count >= allowedMovers) break;
            if (gain < MinimumGainToLeaveTheFiringLine || movers.ContainsKey(index)) continue;

            foreach (float bearing in Bearings)
            {
                if (!taken.Add(bearing)) continue;
                movers[index] = bearing;
                break;
            }
        }

        Vector3 axis = Horizontal(input.Threat - Centre(members));
        axis = axis.LengthSquared() <= 1e-6f ? Vector3.UnitZ : Vector3.Normalize(axis);

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (!movers.TryGetValue(i, out float bearing))
            {
                orders.Add(new SquadTacticalOrder(
                    member.ActorId, SquadRole.BaseOfFire, member.Position, 0f, member.BoundIndex));
                continue;
            }

            orders.Add(new SquadTacticalOrder(
                member.ActorId,
                SquadRole.Bound,
                ApproachPosition(
                    input.Threat,
                    axis,
                    bearing,
                    member.BoundIndex,
                    Horizontal(member.Position, input.Threat),
                    WeaponEffectiveness.PreferredRange(member.Weapon, member.SkillFactor)),
                bearing,
                member.BoundIndex));
        }
    }

    /// <summary>
    /// Where a bound ends: on its own bearing around the threat, and always CLOSER than the mover
    /// already is.
    ///
    /// The standoff used to come from OpeningStandoff and the bound index alone, which produced an
    /// orbit rather than an assault. A man already at 55 m was sent to a point 45 m out on a bearing
    /// 60 degrees around — almost entirely lateral — and because the index only advances when a bound
    /// completes, and completions are rare, the standoff stayed pinned at 45 m indefinitely. Measured,
    /// a six-man squad moved 20-35 m each over thirty seconds and closed 0.4 m, with two men ending
    /// up further away than they started.
    ///
    /// Clamping to the mover's current range makes every bound close by at least BoundLength, so the
    /// squad converges whether or not anybody's bound index is being maintained correctly.
    /// </summary>
    public static Vector3 ApproachPosition(
        Vector3 threat,
        Vector3 axis,
        float bearing,
        int boundIndex,
        float currentRange,
        float preferredStandoff)
    {
        // Never further out than the mover already is. The standoff is a floor on how close the
        // squad will deliberately CLOSE, not a distance it will back off to: a man already inside it
        // was being pushed back out — 11 m to 12 m — which is the orbiting bug again at knife range.
        // Found by fuzzing, not by any hand-written scenario.
        float floor = MathF.Max(ClosestPlannedStandoff, preferredStandoff);
        float standoff = MathF.Min(
            currentRange,
            MathF.Max(
                floor,
                MathF.Min(
                    currentRange - BoundLength,
                    OpeningStandoff - MathF.Max(0, boundIndex) * BoundLength)));

        float cos = MathF.Cos(bearing);
        float sin = MathF.Sin(bearing);
        var rotated = new Vector3(
            axis.X * cos - axis.Z * sin,
            0f,
            axis.X * sin + axis.Z * cos);

        return threat - rotated * standoff;
    }

    /// <summary>Named <c>Against</c>, not <c>Engagement</c>: a method sharing a name with the type it
    /// returns makes every call site inside a collection expression ambiguous to read.</summary>
    /// <summary>
    /// One member's view of the squad's threat.
    ///
    /// <paramref name="squadSize"/> is the term that makes numerical advantage mean something. A
    /// threat shoots one man at a time, so the chance it is shooting at THIS man is roughly one over
    /// the number of men it has to choose between — and Engagement.TheirTargetingLikelihood already
    /// means exactly that.
    ///
    /// Without it every man priced incoming fire as though he were facing the enemy alone, so six
    /// men against one rifleman each expected the full weight of his fire and concluded that closing
    /// was as dangerous for them as for a lone man. That is why a squad that outnumbered a defender
    /// six to one would not assault him, and it is the "when you outnumber your opponent, flank"
    /// doctrine appearing as arithmetic rather than as a rule.
    /// </summary>
    private static Engagement Against(
        in SquadPlanInput input,
        float range,
        SelfExposure exposure,
        int squadSize,
        float threatExtraMoa = 0f)
        => new(
            range,
            input.ThreatWeapon,
            threatExtraMoa,
            TargetExposure.Full,
            exposure,
            1f / MathF.Max(1, squadSize));

    private static Vector3 Centre(IReadOnlyList<SquadMemberState> members)
    {
        Vector3 sum = Vector3.Zero;
        foreach (var member in members) sum += member.Position;
        return sum / members.Count;
    }

    private static Vector3 Horizontal(Vector3 value) => value with { Y = 0f };

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
