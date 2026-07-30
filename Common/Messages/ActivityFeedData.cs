using Riptide;

namespace Demiurge;

/// <summary>A short server-authored event shown in every connected client's activity feed.</summary>
public struct ActivityFeedData : IMessageSerializable
{
    public string Text;

    public void Serialize(Message message) => message.AddString(Text);

    public void Deserialize(Message message) => Text = message.GetString();
}
