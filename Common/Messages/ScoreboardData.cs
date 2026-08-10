using Demiurge.Net;

namespace Demiurge;

/// <summary>One actor's line on the board.</summary>
public struct ScoreboardEntry : IMessageSerializable
{
    public ushort ActorId;
    public byte Team;

    /// <summary>Whether this actor is an NPC. A byte rather than a bool so the row stays a fixed
    /// eight bytes and there is somewhere to put the next flag without moving anything.</summary>
    public byte Flags;

    public ushort Kills;
    public ushort Deaths;

    public const byte MobFlag = 1;

    public bool IsMob => (Flags & MobFlag) != 0;

    public void Serialize(Message message)
    {
        message.AddUShort(ActorId);
        message.AddByte(Team);
        message.AddByte(Flags);
        message.AddUShort(Kills);
        message.AddUShort(Deaths);
    }

    public void Deserialize(Message message)
    {
        ActorId = message.GetUShort();
        Team = message.GetByte();
        Flags = message.GetByte();
        Kills = message.GetUShort();
        Deaths = message.GetUShort();
    }
}

/// <summary>
/// The whole match roster, every time.
///
/// A SNAPSHOT rather than a stream of deltas, and that is the point rather than laziness: delivery
/// here is at-least-once and unordered, so "add one to actor 7's kills" applied twice is wrong and a
/// duplicate is something the transport does on purpose. A complete roster applied twice is the same
/// roster. It also means a client that missed one never drifts — the next kill anywhere on the map
/// repairs it — and that a joining client needs no separate catch-up path, just one send.
///
/// It carries TEAM as well as the tally, so it is the one place the client learns that somebody
/// changed sides. PlayerSpawn cannot say it: a repeat spawn for a live actor is deliberately ignored
/// (see PlayerRegistry), which is right for spawns and would have silently swallowed the change.
///
/// Cost is eight bytes an actor — about 270 for a full thirty-two NPC match — sent when the board
/// actually changes, which is a kill, a death, a join, a departure or a side change.
/// </summary>
public struct ScoreboardData : IMessageSerializable
{
    public ScoreboardEntry[] Entries;

    public void Serialize(Message message)
    {
        var entries = Entries ?? [];
        message.AddUShort((ushort)entries.Length);
        foreach (var entry in entries) message.AddSerializable(entry);
    }

    public void Deserialize(Message message)
    {
        int count = message.GetUShort();
        Entries = new ScoreboardEntry[count];
        for (int i = 0; i < count; i++)
            Entries[i] = message.GetSerializable<ScoreboardEntry>();
    }
}
