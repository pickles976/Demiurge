namespace Demiurge;

/// <summary>
/// How much of THIS actor a shooter can reach where it stands, 0..1.
///
/// Distinct from <see cref="TargetExposure"/> on purpose, and the reason is a bug rather than
/// tidiness. Both quantities are fractions of a silhouette, both are naturally called "exposure",
/// and both were plain floats — so feeding one where the other belonged compiled silently. It
/// happened twice in one afternoon: a squad was told it was 80% protected whenever the man it was
/// shooting at happened to be behind cover, which made holding look free and manoeuvre look
/// suicidal, and six men dug in against one rifleman rather than flanking him.
///
/// Crossing between the two is a change of PERSPECTIVE, not a cast, so it only happens through
/// <see cref="AsTarget"/> — at which point the swap is visible in the source instead of silent.
/// </summary>
public readonly record struct SelfExposure
{
    public float Fraction { get; }

    private SelfExposure(float fraction) => Fraction = Math.Clamp(fraction, 0f, 1f);

    public static SelfExposure Of(float fraction) => new(fraction);

    /// <summary>Standing in the open with nothing between you and the shooter.</summary>
    public static readonly SelfExposure Full = new(1f);

    /// <summary>
    /// Seen from the far end: what I am worth shooting at. Named because this is the one legitimate
    /// crossing between the two perspectives, and it belongs in the damage-taken term where the
    /// enemy is the shooter and I am the target.
    /// </summary>
    public TargetExposure AsTarget() => TargetExposure.Of(Fraction);

    public override string ToString() => $"self {Fraction:P0}";
}

/// <summary>
/// How much of the actor being SHOT AT can be reached, 0..1. Scales the target radius in
/// <see cref="WeaponEffectiveness"/>, so a man peeking over a parapet is genuinely harder to hit
/// rather than merely harder to see.
/// </summary>
public readonly record struct TargetExposure
{
    public float Fraction { get; }

    private TargetExposure(float fraction) => Fraction = Math.Clamp(fraction, 0f, 1f);

    public static TargetExposure Of(float fraction) => new(fraction);

    /// <summary>Fully presented. Also the right answer when the question is a weapon's REACH rather
    /// than a particular target's cover — see CombatBehavior's engagement test.</summary>
    public static readonly TargetExposure Full = new(1f);

    public override string ToString() => $"target {Fraction:P0}";
}
