using Demiurge.Net;

namespace Demiurge;

public struct CommandRequestData : IMessageSerializable
{
    public uint RequestId;
    public string Command;

    public void Serialize(Message message)
    {
        message.AddUInt(RequestId);
        message.AddString(Command);
    }

    public void Deserialize(Message message)
    {
        RequestId = message.GetUInt();
        Command = message.GetString();
    }
}
