using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct PlayerDespawnData : IMessageSerializable
    {
        public ushort PlayerId;
        public uint Tick;

        public void Serialize(Message message)
        {
            message.AddUShort(PlayerId);
            message.AddUInt(Tick);
        }

        public void Deserialize(Message message)
        {
            PlayerId = message.GetUShort();
            Tick = message.GetUInt();
        }
    }
}