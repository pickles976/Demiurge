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
    /// How far from a flag a man is still part of what happens to it.
    ///
    /// Derived, not chosen: the ground a man covers in the time a capture takes. Inside it he can
    /// intervene before the clock runs out, outside it he cannot, and that is the only thing the
    /// planner wants to know about him. <see cref="FlagConfig.CaptureRadius"/> is four metres and
    /// answers a different question — who is turning the clock this instant — so using it as a
    /// measure of "is this flag being handled" made six men in holes twenty metres out invisible.
    /// </summary>
    public static float InfluenceRadius
        => PlayerMovement.WalkSpeed * FlagConfig.CaptureSeconds;

    /// <summary>
    /// How many flags of bleed differential this flag changing hands moves.
    ///
    /// Two whenever it can swap sides — they lose one and we gain one — and one for a neutral, which
    /// only has our half to give. That is now the WHOLE of it: which side currently holds it decides
    /// the size of the swing, and nothing else.
    ///
    /// A quiet friendly flag used to swing zero, on the reasoning that it was not going to change
    /// hands. That reasoning was a prediction, and it was hidden inside what is supposed to be a
    /// magnitude. Its effect was the reported pile-up: with our own flags priced at exactly nothing,
    /// every squad on the map was drawn to whichever one or two flags the enemy happened to hold,
    /// the rear went unwatched, and it flipped the moment anybody wandered onto it — at which point
    /// it became worth two and the whole force turned round. Defence is not a special case and does
    /// not need one; it needs to be priced. WHEN a flag's fate is decided is a discount, and it now
    /// lives in <see cref="SecondsUntilDecided"/> where the rest of the timing does.
    /// </summary>
    public static float Swing(int team, in StrategicFlag flag)
        => flag.OwnerTeam == FlagConfig.NeutralTeam ? 1f : 2f;

    private static bool IsEnemyCapturing(int team, in StrategicFlag flag)
        => flag.CapturingTeam != FlagConfig.NeutralTeam && flag.CapturingTeam != team;

    /// <summary>
    /// Seconds until this flag's fate is settled if nobody new intervenes — the delay before the
    /// swing above is actually collected.
    ///
    /// The two cases are the same question asked from opposite ends. A flag we do not hold is
    /// decided when WE arrive and turn the clock, so it costs the capture plus whoever is standing
    /// on it. A flag we do hold is decided when THEY arrive to take it, so a rear flag with the
    /// nearest enemy two hundred metres away discounts almost to nothing all by itself, and one with
    /// an enemy squad walking up is worth as much as an attack. That is what makes garrisoning the
    /// rear unattractive without a rule forbidding it, and reinforcing a threatened flag attractive
    /// without a priority class ranking defence above attack.
    /// </summary>
    public static float SecondsUntilDecided(int team, in StrategicFlag flag)
    {
        if (flag.OwnerTeam != team)
            return FlagConfig.CaptureSeconds
                + MathF.Max(0, flag.EnemyPresence) * SecondsPerDefender;

        // Already being taken: it is decided now, and the capture clock is all that is left.
        if (flag.EnemyPresence > 0 || IsEnemyCapturing(team, flag))
            return FlagConfig.CaptureSeconds * (1f - Math.Clamp(flag.Progress, 0f, 1f));

        return MathF.Max(0f, flag.EnemyApproachSeconds) + FlagConfig.CaptureSeconds;
    }

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

        float seconds = MathF.Max(0f, travelSeconds) + SecondsUntilDecided(team, flag);
        if (float.IsPositiveInfinity(seconds)) return 0f;

        // Value now versus value later, as a smooth discount. Never negative and never zero, so a
        // distant flag is merely worth less rather than actively avoided.
        float discounted = swing * TicketsPerSecondPerFlag
            * (PlanningHorizonSeconds / (PlanningHorizonSeconds + seconds));

        // The nth squad gets what the first n-1 left on the table.
        //
        // "Already assigned" now counts the men who are ALREADY THERE as well as the squads this
        // plan has committed, because to the flag they are the same thing and the planner was blind
        // to the first kind. Every plan starts from zero assignments, so a flag that six men were
        // standing on looked exactly as free as an empty one to the next squad — which is the other
        // half of the pile-up, and the half that survived making the value continuous.
        //
        // Halving per squad kept, extended to fractional strength: strictly decreasing, never
        // negative, and identical to the old shift at whole squads. MaxCapturePlayers is the natural
        // unit because a man past it adds nothing to the capture rate.
        float committed = Math.Clamp(squadsAlreadyAssigned, 0, 8)
            + MathF.Max(0, flag.FriendlyApproaching) / (float)FlagConfig.MaxCapturePlayers;
        return discounted * MathF.Pow(0.5f, committed);
    }
}
