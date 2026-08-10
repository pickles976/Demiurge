namespace Demiurge;

/// <summary>An enemy's index in the caller's list, and the most it could possibly be worth.</summary>
public readonly record struct ThreatBound(int Index, float UpperBound);

/// <summary>
/// Deciding which believed enemies are worth spending perception on.
///
/// Every term in <see cref="CombatValue.Taken"/> except exposure is a table lookup needing no ray,
/// and exposure is at most 1. So evaluating the rest with exposure pinned at 1 gives a true UPPER
/// BOUND on that enemy's contribution. Rank by the bound, spend the ray budget from the top, and an
/// enemy left unexamined is one that provably could not have changed the decision by more than the
/// threshold.
///
/// That soundness is the point. "An SMG 100 m away probably doesn't matter" is a guess that fails
/// quietly when it is wrong; "this enemy's maximum possible contribution is 0.4 HP/s" is a fact. It
/// is also why <see cref="WeaponEffectiveness"/> must not overestimate hit probability — the bound
/// inherits any optimism in the curve.
///
/// Doubles as target selection: the same ranking read from the other end is "most dangerous to me
/// right now", so one sorted list serves both.
/// </summary>
public static class ThreatRanking
{
    /// <summary>
    /// The most this enemy could take from us, if it turned out to be looking straight at a fully
    /// exposed target. No raycast.
    /// </summary>
    public static float UpperBound(in Engagement engagement)
        => WeaponEffectiveness.Best(
                engagement.TheirWeapon,
                engagement.Range,
                TargetExposure.Full,
                engagement.TheirExtraMoa,
                skillFactor: 1f).DamagePerSecond
           * Math.Clamp(engagement.TheirTargetingLikelihood, 0f, 1f);

    /// <summary>
    /// Fills <paramref name="into"/> with the most dangerous enemies first, and returns how many
    /// were written. Writes at most <c>into.Length</c> entries, so a caller with a ray budget passes
    /// a span that size and gets exactly the enemies worth spending it on.
    /// </summary>
    public static int Rank(IReadOnlyList<Engagement> engagements, Span<ThreatBound> into)
    {
        if (into.Length == 0 || engagements.Count == 0) return 0;

        // Insertion into a bounded, already-sorted destination. The budget is single digits, so this
        // beats allocating and sorting the full list every tick for every NPC.
        int count = 0;
        for (int i = 0; i < engagements.Count; i++)
        {
            var candidate = new ThreatBound(i, UpperBound(engagements[i]));
            if (count == into.Length && candidate.UpperBound <= into[count - 1].UpperBound)
                continue;

            int position = count < into.Length ? count : count - 1;
            while (position > 0 && into[position - 1].UpperBound < candidate.UpperBound)
            {
                into[position] = into[position - 1];
                position--;
            }
            into[position] = candidate;
            if (count < into.Length) count++;
        }

        return count;
    }
}
