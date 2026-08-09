namespace Demiurge;

/// <summary>
/// What a flag is worth, in TICKETS PER SECOND.
///
/// The currency is not a modelling choice — it is the win condition. <see cref="ConquestConfig"/>
/// charges a team one ticket every <see cref="ConquestConfig.TicketBleedSeconds"/> for every flag it
/// is behind by, so one net flag is worth exactly <see cref="TicketsPerSecondPerFlag"/> and nothing
/// else on the map is worth anything except through that. It is also the same unit combat is in once
/// converted: a body costs one ticket at <c>TicketSystem.ChargeRespawn</c>, so health per second
/// divided by health per death IS tickets per second — see <see cref="ThreatResponse"/>. Strategy,
/// combat and movement therefore share one scale.
///
/// This replaces a ladder of discrete priorities keyed on presence booleans. Two things went wrong
/// with that and both were structural rather than tuning:
///
///  - the second squad sent to a contested flag scored above the first squad sent to an empty one
///    (800 against 600), and sending squads is what made a flag contested — a loop that concentrated
///    the whole force on one or two objectives and never took free ground;
///  - the classes were step functions of a noisy input, so one man crossing a capture radius
///    reclassified a flag and reshuffled every assignment.
///
/// Both disappear from a continuous marginal value rather than being tuned out of a table.
/// </summary>
public static class StrategicValue
{
    /// <summary>One net flag of advantage, as a rate. Straight out of the bleed rule.</summary>
    public static float TicketsPerSecondPerFlag => 1f / ConquestConfig.TicketBleedSeconds;

    /// <summary>
    /// How far ahead the commander values. A flag that takes this long to secure is worth about half
    /// what an instant one is; it is the horizon of the discount below, not a deadline.
    ///
    /// Sized against a round rather than picked: 300 tickets bleeding at one per three seconds is
    /// many minutes, and a squad crosses this map in well under two.
    /// </summary>
    public const float PlanningHorizonSeconds = 60f;

    /// <summary>
    /// Expected seconds of fighting per enemy already on a flag, added to the time before it starts
    /// paying. Deliberately smooth: it is what makes a defended flag less attractive than an empty
    /// one WITHOUT reclassifying it as "contested".
    /// </summary>
    public const float SecondsPerDefender = 8f;

    /// <summary>
    /// How many flags of bleed differential taking this flag moves.
    ///
    /// Two for an enemy-held flag, because they lose one and we gain one; one for a neutral. A
    /// friendly flag with enemies on it is the same two-flag swing seen from the other end — losing
    /// it would cost exactly what retaking it would gain — which is why defence needs no separate
    /// priority class to outrank an attack. A quiet friendly flag swings nothing: it is not going to
    /// change hands, so garrisoning it buys no tickets.
    /// </summary>
    public static float Swing(int team, in StrategicFlag flag)
    {
        if (flag.OwnerTeam == team)
            return flag.EnemyPresence > 0 || IsEnemyCapturing(team, flag) ? 2f : 0f;
        return flag.OwnerTeam == FlagConfig.NeutralTeam ? 1f : 2f;
    }

    private static bool IsEnemyCapturing(int team, in StrategicFlag flag)
        => flag.CapturingTeam != FlagConfig.NeutralTeam && flag.CapturingTeam != team;

    /// <summary>
    /// What assigning one MORE squad to this flag is worth, in tickets per second.
    ///
    /// Marginal, not total, and that is the whole fix. The first squad on an undefended flag converts
    /// the entire swing; the second converts whatever the first left, which on a flag already being
    /// taken is nearly nothing. Diminishing returns are produced by the model rather than by a
    /// hand-written rule that two squads per objective is enough.
    /// </summary>
    public static float Marginal(
        int team,
        in StrategicFlag flag,
        int squadsAlreadyAssigned,
        float travelSeconds)
    {
        float swing = Swing(team, flag);
        if (swing <= 0f) return 0f;

        // Securing takes travel, then the capture clock, then however long the defenders last.
        float seconds = MathF.Max(0f, travelSeconds)
            + FlagConfig.CaptureSeconds
            + MathF.Max(0, flag.EnemyPresence) * SecondsPerDefender;

        // Value now versus value later, as a smooth discount. Never negative and never zero, so a
        // distant flag is merely worth less rather than actively avoided.
        float discounted = swing * TicketsPerSecondPerFlag
            * (PlanningHorizonSeconds / (PlanningHorizonSeconds + seconds));

        // The nth squad gets what the first n-1 left on the table. Halving per squad is the simplest
        // form with the property that matters — strictly decreasing, never negative — and
        // FlagConfig.MaxCapturePlayers makes anything past a couple of squads genuinely idle.
        return discounted / (1 << Math.Clamp(squadsAlreadyAssigned, 0, 8));
    }
}
