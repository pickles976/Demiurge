using System.Numerics;
using Demiurge.Net;

namespace Demiurge;

/// <summary>One enemy a team believes it has located, and how sure it is.</summary>
/// <param name="Confidence">255 when just observed, fading to nothing as the belief ages. It is the
/// same fade <see cref="ContactMemory"/> already applies, quantised — a marker that vanished at full
/// strength would read as the enemy teleporting rather than as the trail going cold.</param>
public struct TeamContact : IMessageSerializable
{
    public ushort ActorId;
    public Vector3 Position;
    public byte Confidence;

    public void Serialize(Message message)
    {
        message.AddUShort(ActorId);
        message.AddVector3(Position);
        message.AddByte(Confidence);
    }

    public void Deserialize(Message message)
    {
        ActorId = message.GetUShort();
        Position = message.GetVector3();
        Confidence = message.GetByte();
    }
}

/// <summary>
/// What one team knows about where the enemy is — the picture its minimap draws.
///
/// This is BELIEF, not truth, and that is the whole reason it exists as its own message rather than
/// the client filtering the actor positions it already has. The client is sent every actor's
/// position because it has to draw them, so a minimap built from that would be a wallhack with a
/// border. What a team has actually earned — a man its NPCs can see, a rifle it heard go off nearby —
/// is a smaller set that only the server can compute, and this is that set.
///
/// A SNAPSHOT per team, so applying it twice equals applying it once and a lost one costs half a
/// second. <see cref="Tick"/> is carried because it goes unreliable: an older snapshot overtaking a
/// newer one would rewind the picture, and dropping it is free.
/// </summary>
public struct TeamIntelData : IMessageSerializable
{
    public uint Tick;
    public TeamContact[] Contacts;

    public void Serialize(Message message)
    {
        message.AddUInt(Tick);
        var contacts = Contacts ?? [];
        message.AddUShort((ushort)contacts.Length);
        foreach (var contact in contacts) message.AddSerializable(contact);
    }

    public void Deserialize(Message message)
    {
        Tick = message.GetUInt();
        int count = message.GetUShort();
        Contacts = new TeamContact[count];
        for (int i = 0; i < count; i++)
            Contacts[i] = message.GetSerializable<TeamContact>();
    }
}
