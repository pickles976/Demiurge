namespace Demiurge;

public enum CoverKind : byte
{
    None,
    Concealment,
    FightingPosition,
    CornerFightingPosition,
    /// <summary>A temporary forward navigation point used to close ineffective rifle range.</summary>
    Advance,
}

/// <summary>
/// Precomputed facts for one tactical-position candidate. World queries stay on the server; this
/// value and the scoring remain pure so weights and classification can be tested headlessly.
/// </summary>
public readonly record struct CoverFacts(
    int ThreatCount,
    int CrouchedExposures,
    int StandingExposures,
    bool CanShootBack,
    float TravelSeconds,
    int EscapeRoutes);

public readonly record struct CoverRating(CoverKind Kind, float Score)
{
    public bool Usable => Kind != CoverKind.None && float.IsFinite(Score);
}

/// <summary>
/// Classifies and ranks generated tactical positions. A fighting position hides a crouched agent
/// from every sampled threat but exposes at least one target when standing. Full concealment hides
/// both poses and is a lower-value movement destination because it cannot support direct fire.
/// </summary>
public static class CoverScore
{
    private const float FightingPositionBase = 100f;
    private const float CornerFightingPositionBase = 112f;
    private const float ConcealmentBase = 55f;
    private const float ShootBackBonus = 10f;
    private const float TravelPenaltyPerSecond = 2.5f;
    private const float MaximumTravelPenalty = 35f;
    private const float EscapeRouteBonus = 2f;
    private const int MaximumRewardedEscapeRoutes = 4;
    private const float TrappedPenalty = 16f;

    public static CoverRating Evaluate(in CoverFacts facts)
    {
        if (facts.ThreatCount <= 0
            || facts.CrouchedExposures < 0
            || facts.StandingExposures < 0
            || facts.CrouchedExposures > facts.ThreatCount
            || facts.StandingExposures > facts.ThreatCount
            || !float.IsFinite(facts.TravelSeconds)
            || facts.TravelSeconds < 0f
            || facts.EscapeRoutes < 0)
            return new CoverRating(CoverKind.None, float.NegativeInfinity);

        CoverKind kind =
            facts.CrouchedExposures == 0 && facts.StandingExposures > 0 && facts.CanShootBack
                ? CoverKind.FightingPosition
                : facts.CrouchedExposures == 0
                  && facts.StandingExposures == 0
                  && facts.CanShootBack
                    ? CoverKind.CornerFightingPosition
                : facts.CrouchedExposures == 0
                  && facts.StandingExposures == 0
                    ? CoverKind.Concealment
                    : CoverKind.None;
        if (kind == CoverKind.None)
            return new CoverRating(kind, float.NegativeInfinity);

        float score = kind switch
        {
            CoverKind.CornerFightingPosition => CornerFightingPositionBase,
            CoverKind.FightingPosition => FightingPositionBase,
            _ => ConcealmentBase,
        };
        if (facts.CanShootBack) score += ShootBackBonus;
        score -= MathF.Min(MaximumTravelPenalty, facts.TravelSeconds * TravelPenaltyPerSecond);
        score += Math.Min(facts.EscapeRoutes, MaximumRewardedEscapeRoutes) * EscapeRouteBonus;
        if (facts.EscapeRoutes == 0) score -= TrappedPenalty;
        return new CoverRating(kind, score);
    }
}
