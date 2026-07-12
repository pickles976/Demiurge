using Riptide;

namespace Demiurge
{
    public struct ObjectDespawnData : IMessageSerializable
    {
        public uint NetworkId;

        public void Serialize(Message message) => message.AddUInt(NetworkId);
        public void Deserialize(Message message) => NetworkId = message.GetUInt();
    }
}