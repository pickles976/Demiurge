namespace Demiurge;

/// <summary>
/// Whether a threat is worth answering, or merely worth taking cover from.
///
/// The distinction did not exist. Any round passing near an NPC published a squad-wide contact at
/// whatever range it came from, and because a weapon that cannot reach yields a zero firing solution
/// — which IS the decision to close — the squad then advanced on a shooter it had no prospect of
/// reaching. Being outranged was what made them charge.
///
/// Both sides are priced in tickets per second, which is what makes them comparable at all:
/// <see cref="CombatValue"/> is in health per second, a body costs one ticket when it respawns, and
/// a flag is worth <see cref="StrategicValue.TicketsPerSecondPerFlag"/>. So "is this shooter worth a
/// flag?" is a real question with a real answer rather than a judgement call encoded as a range
/// constant — and the answer moves correctly when either the weapon table or the bleed rule changes.
/// </summary>
public static class ThreatResponse
{
    /// <summary>
    /// Health per second, as tickets per second. One body is one ticket at
    /// <c>TicketSystem.ChargeRespawn</c>, so losing a man's whole health is losing a ticket.
    /// </summary>
    public static float TicketsPerSecond(float healthPerSecond, int maximumHealth)
        => maximumHealth <= 0 ? 0f : MathF.Max(0f, healthPerSecond) / maximumHealth;

    /// <summary>
    /// A man is worth this much health, for the conversion above. A constant rather than the live
    /// actor's health so the model stays pure and a wounded man does not become cheaper to lose.
    /// </summary>
    public const int NominalHealth = 100;

    /// <summary>
    /// Whether answering this threat beats carrying on with the objective.
    ///
    /// Answering is worth what it stops him taking from us; carrying on is worth the objective. Both
    /// in tickets per second, so this is a comparison rather than a rule.
    /// </summary>
    public static bool IsWorthAnswering(
        in Combatant self,
        in Engagement threat,
        float objectiveValue)
        => TicketsPerSecond(CombatValue.Taken(self, [threat]), NominalHealth)
           > MathF.Max(0f, objectiveValue);
}
