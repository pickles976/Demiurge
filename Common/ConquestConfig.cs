namespace Demiurge;

/// <summary>
/// The conquest ticket economy. In Common because the server enforces it and the client draws it,
/// and neither should carry its own copy of the numbers.
/// </summary>
public static class ConquestConfig
{
    public const int StartingTickets = 200;

    /// <summary>How often the map's flag balance is charged against the losing side's tickets.</summary>
    public const float TicketBleedSeconds = 3f;

    /// <summary>
    /// Tickets a team loses per bleed interval: one for every flag its strongest rival holds beyond
    /// its own. Holding as many flags as everyone else costs nothing, so a team only bleeds while it
    /// is behind on the map — the deficit is the rate, not the score.
    ///
    /// Written against a flag count per team rather than "the enemy" so it stays defined with more
    /// than two teams: the worst deficit against any one rival is what you pay.
    /// </summary>
    public static int BleedFor(int team, IReadOnlyDictionary<int, int> flagsByTeam)
    {
        int own = flagsByTeam.GetValueOrDefault(team);
        int worst = 0;
        foreach (var (other, count) in flagsByTeam)
        {
            if (other == team || other == FlagConfig.NeutralTeam) continue;
            worst = Math.Max(worst, count - own);
        }
        return worst;
    }
}
