using Riptide;

namespace Demiurge
{
    public struct ObjectSpawnData: IMessageSerializable
    {
        public uint NetworkId;
        public ObjectType Type;
        public ComponentBundle State;

        public void Serialize(Message message)
        {
            message.AddUInt(NetworkId);
            message.AddUShort((ushort)Type);
            message.AddSerializable(State);
        }

        public void Deserialize(Message message)
        {
            NetworkId = message.GetUInt();
            Type = (ObjectType)message.GetUShort();
            State = message.GetSerializable<ComponentBundle>();
        }
    }

}