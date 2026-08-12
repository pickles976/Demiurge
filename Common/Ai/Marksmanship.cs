namespace Demiurge;

/// <summary>
/// How well one man shoots, as a multiplier on his weapon's <see cref="BallisticsStats.SightingMoa"/>.
/// One is a competent soldier and larger is worse; it scales execution only and never touches any
/// decision the AI makes.
///
/// Every NPC used to be exactly 1.0 — the field existed, nothing ever assigned it — so every man on
/// the map held every weapon equally well and a squad's fire was uniform. Handing them a spread is
/// not a difficulty setting: it is the input the rest of the model was already written to take.
/// <c>MobSystem</c>'s "does this man close the distance?" test has always read
/// <see cref="WeaponEffectiveness.PreferredRange"/> AT HIS SKILL, so a poor shot with a rifle now
/// wants to fight nearer than a good one and closes without anybody adding a rule saying he should.
/// </summary>
public static class Marksmanship
{
    /// <summary>
    /// The band, chosen to average 1.3 — deliberately worse than the old flat 1.0.
    ///
    /// Measured hit rates at 1.0 were 47% per round at 40 m and 24% at 90 m for the standard rifle,
    /// against the 6–25% at 283 m that <see cref="BallisticsStats.SightingMoa"/> is calibrated to.
    /// The spread is what stops a squad being eight identical shooters; the shift is what pays for
    /// the stance and burst work that made them deadlier.
    /// </summary>
    public const float BestSkillFactor = 0.9f;

    /// <inheritdoc cref="BestSkillFactor"/>
    public const float WorstSkillFactor = 1.7f;

    /// <summary>
    /// This man's skill, fixed for as long as he exists.
    ///
    /// Hashed from his id rather than drawn from a shared sequence, because a draw off the squad's
    /// RNG depends on how many other things asked it for a number first — so the same man would
    /// shoot differently depending on the order the map happened to spawn him in, and a scenario
    /// would stop being reproducible the moment anything else consumed a random number.
    /// </summary>
    public static float SkillFactorFor(int seed, int actorId)
        => BestSkillFactor
            + (WorstSkillFactor - BestSkillFactor)
                * (Spread.ShotSeed((ushort)actorId, (uint)seed) / (float)uint.MaxValue);
}
