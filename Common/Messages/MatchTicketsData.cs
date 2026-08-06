using Demiurge.Net;

namespace Demiurge;

public struct TeamTickets : IMessageSerializable
{
    public ushort Team;
    public ushort Tickets;

    public void Serialize(Message message)
    {
        message.AddUShort(Team);
        message.AddUShort(Tickets);
    }

    public void Deserialize(Message message)
    {
        Team = message.GetUShort();
        Tickets = message.GetUShort();
    }
}

/// <summary>
/// Every team's remaining tickets. Sent whole rather than as a delta, so a client that misses one
/// (or receives it twice) still ends up displaying what the server believes.
/// </summary>
public struct MatchTicketsData : IMessageSerializable
{
    public TeamTickets[] Teams;

    public void Serialize(Message message)
    {
        var teams = Teams ?? [];
        message.AddByte((byte)teams.Length);
        foreach (var team in teams) message.AddSerializable(team);
    }

    public void Deserialize(Message message)
    {
        int count = message.GetByte();
        Teams = new TeamTickets[count];
        for (int i = 0; i < count; i++) Teams[i] = message.GetSerializable<TeamTickets>();
    }
}
