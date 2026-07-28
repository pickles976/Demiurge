using Riptide;

namespace Demiurge;

public struct CommandResultData : IMessageSerializable
{
    public uint RequestId;
    public bool Success;
    public string Output;

    public void Serialize(Message message)
    {
        message.AddUInt(RequestId);
        message.AddBool(Success);
        message.AddString(Output);
    }

    public void Deserialize(Message message)
    {
        RequestId = message.GetUInt();
        Success = message.GetBool();
        Output = message.GetString();
    }
}
