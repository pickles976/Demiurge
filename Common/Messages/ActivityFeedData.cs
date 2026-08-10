using Demiurge.Net;

namespace Demiurge;

/// <summary>
/// One run of feed text drawn in a single colour. <see cref="Team"/> 0 means "no team" — the
/// connecting words, coordinates and reasons that belong to nobody — and is drawn plain.
/// </summary>
public struct ActivityFeedSegment : IMessageSerializable
{
    public string Text;
    public byte Team;

    public void Serialize(Message message)
    {
        message.AddString(Text ?? string.Empty);
        message.AddByte(Team);
    }

    public void Deserialize(Message message)
    {
        Text = message.GetString();
        Team = message.GetByte();
    }
}

/// <summary>
/// A short server-authored event shown in every connected client's activity feed. Sent as segments
/// rather than one string so the client can colour each actor by its team without having to parse
/// the sentence back apart — the server already knows who it is talking about.
/// </summary>
public struct ActivityFeedData : IMessageSerializable
{
    public ActivityFeedSegment[] Segments;

    public void Serialize(Message message)
    {
        var segments = Segments ?? [];
        message.AddByte((byte)segments.Length);
        foreach (var segment in segments) message.AddSerializable(segment);
    }

    public void Deserialize(Message message)
    {
        int count = message.GetByte();
        Segments = new ActivityFeedSegment[count];
        for (int i = 0; i < count; i++)
            Segments[i] = message.GetSerializable<ActivityFeedSegment>();
    }
}
