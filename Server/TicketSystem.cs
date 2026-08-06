using Demiurge.Net;

namespace Demiurge.GameServer;

/// <summary>
/// Conquest tickets: the score that actually ends a round. Each team starts at
/// <see cref="ConquestConfig.StartingTickets"/> and bleeds every
/// <see cref="ConquestConfig.TicketBleedSeconds"/> by how many flags it is behind on the map, so
/// holding ground is what wins rather than kills.
///
/// The rule itself lives in <see cref="ConquestConfig"/>; this class owns the clock, the counters,
/// and who has been told about them. It does not end the round — reaching zero is currently only
/// visible, which is enough to test the economy.
/// </summary>
public sealed class TicketSystem
{
    private readonly INetServer server;
    private readonly FlagSystem flags;
    private readonly Dictionary<int, int> tickets = [];
    private float sinceBleed;

    public TicketSystem(INetServer server, FlagSystem flags, IEnumerable<int> teams)
    {
        this.server = server;
        this.flags = flags;
        foreach (int team in teams)
            if (team > FlagConfig.NeutralTeam)
                tickets[team] = ConquestConfig.StartingTickets;
    }

    public IReadOnlyDictionary<int, int> Tickets => tickets;

    public void Tick(float dt)
    {
        if (tickets.Count == 0 || !float.IsFinite(dt) || dt <= 0f) return;

        sinceBleed += dt;
        if (sinceBleed < ConquestConfig.TicketBleedSeconds) return;
        // One charge per interval however long the tick was: a server hitch is not a reason to
        // take three seconds of tickets off a team at once.
        sinceBleed -= ConquestConfig.TicketBleedSeconds;

        // Counted here rather than passed in every tick: this runs once per bleed interval, so the
        // scan and its dictionary happen 0.03 times a tick instead of once.
        var flagsByTeam = flags.ControlledCounts();
        foreach (int team in tickets.Keys.ToArray())
        {
            int bleed = ConquestConfig.BleedFor(team, flagsByTeam);
            if (bleed <= 0) continue;
            tickets[team] = Math.Max(0, tickets[team] - bleed);
        }

        // Sent every interval, not only when a number moved. It is one tiny message every three
        // seconds, and it means a client that missed its join-time catch-up — or subscribed a frame
        // late — repairs itself rather than showing a blank score for the rest of the match.
        server.SendToAll(CreateMessage());
    }

    /// <summary>Catches a joining client up, since the next bleed may be three seconds away.</summary>
    public void SendTo(ushort clientId)
    {
        if (tickets.Count == 0) return;
        server.Send(CreateMessage(), clientId);
    }

    private Message CreateMessage()
    {
        var message = Message.Create(MessageSendMode.Reliable, ServerToClientId.MatchTickets);
        message.AddSerializable(new MatchTicketsData
        {
            Teams = [.. tickets
                .OrderBy(entry => entry.Key)
                .Select(entry => new TeamTickets
                {
                    Team = (ushort)entry.Key,
                    Tickets = (ushort)entry.Value,
                })],
        });
        return message;
    }
}
