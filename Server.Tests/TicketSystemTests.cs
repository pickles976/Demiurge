using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

public class TicketSystemTests
{
    [Fact]
    public void EqualFlagCountsCostNobodyTickets()
    {
        var (flags, tickets) = Match();
        Capture(flags, new Vector3(0f, 0f, 0f), team: 1);
        Capture(flags, new Vector3(50f, 0f, 0f), team: 2);

        Bleed(tickets, intervals: 4);

        Assert.Equal(ConquestConfig.StartingTickets, tickets.Tickets[1]);
        Assert.Equal(ConquestConfig.StartingTickets, tickets.Tickets[2]);
    }

    [Fact]
    public void TheTeamBehindOnFlagsBleedsItsDeficitPerInterval()
    {
        var (flags, tickets) = Match();
        // Team 2 holds three, team 1 holds two: team 1 is one flag down.
        Capture(flags, new Vector3(0f, 0f, 0f), team: 1);
        Capture(flags, new Vector3(50f, 0f, 0f), team: 1);
        Capture(flags, new Vector3(100f, 0f, 0f), team: 2);
        Capture(flags, new Vector3(150f, 0f, 0f), team: 2);
        Capture(flags, new Vector3(200f, 0f, 0f), team: 2);

        Bleed(tickets, intervals: 3);

        Assert.Equal(ConquestConfig.StartingTickets - 3, tickets.Tickets[1]);
        Assert.Equal(ConquestConfig.StartingTickets, tickets.Tickets[2]);
    }

    [Fact]
    public void NeutralFlagsCountForNobodyAndTicketsStopAtZero()
    {
        var (flags, tickets) = Match();
        // Two flags for team 2, one neutral: team 1 is two down, not one.
        Capture(flags, new Vector3(0f, 0f, 0f), team: 2);
        Capture(flags, new Vector3(50f, 0f, 0f), team: 2);
        flags.Spawn(new Vector3(100f, 0f, 0f));

        Bleed(tickets, intervals: 2);
        Assert.Equal(ConquestConfig.StartingTickets - 4, tickets.Tickets[1]);

        Bleed(tickets, intervals: ConquestConfig.StartingTickets);
        Assert.Equal(0, tickets.Tickets[1]);
        Assert.Equal(ConquestConfig.StartingTickets, tickets.Tickets[2]);
    }

    [Fact]
    public void TicketsOnlyMoveWhenTheIntervalElapses()
    {
        var (flags, tickets) = Match();
        Capture(flags, Vector3.Zero, team: 2);

        // Nothing before the interval is up, and one charge when it is — not a burst catching up
        // for every tick that passed. The exact tick is left loose on purpose: summing 1/30 in
        // float lands just under three seconds at tick 90, so the charge falls on tick 91.
        int ticks = (int)MathF.Ceiling(ConquestConfig.TicketBleedSeconds * NetworkConfig.TickRate);
        for (int i = 0; i < ticks - 1; i++) tickets.Tick(1f / NetworkConfig.TickRate);
        Assert.Equal(ConquestConfig.StartingTickets, tickets.Tickets[1]);

        tickets.Tick(1f / NetworkConfig.TickRate);
        tickets.Tick(1f / NetworkConfig.TickRate);
        Assert.Equal(ConquestConfig.StartingTickets - 1, tickets.Tickets[1]);
    }

    [Fact]
    public void EveryRespawnCostsItsTeamOneTicketAndStopsAtZero()
    {
        var (_, tickets) = Match();

        tickets.ChargeRespawn(1);
        tickets.ChargeRespawn(1);
        tickets.ChargeRespawn(2);

        Assert.Equal(ConquestConfig.StartingTickets - 2, tickets.Tickets[1]);
        Assert.Equal(ConquestConfig.StartingTickets - 1, tickets.Tickets[2]);

        for (int i = 0; i < ConquestConfig.StartingTickets + 5; i++) tickets.ChargeRespawn(1);
        Assert.Equal(0, tickets.Tickets[1]);

        // A team that is not playing this map is not a team that can be charged.
        tickets.ChargeRespawn(7);
        Assert.False(tickets.Tickets.ContainsKey(7));
    }

    private static (FlagSystem Flags, TicketSystem Tickets) Match()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var flags = new FlagSystem(objects);
        return (flags, new TicketSystem(new NullNetServer(), flags, [1, 2]));
    }

    private static void Capture(FlagSystem flags, Vector3 position, int team)
    {
        flags.Spawn(position);
        Assert.True(flags.TryForceOwner(position, team));
    }

    private static void Bleed(TicketSystem tickets, int intervals)
    {
        for (int i = 0; i < intervals; i++) tickets.Tick(ConquestConfig.TicketBleedSeconds);
    }
}
