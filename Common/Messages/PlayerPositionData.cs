using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct PlayerPositionData : IMessageSerializable
    {
        public ushort PlayerId;
        public Vector3 Position;

        public void Serialize(Message message)
        {
            message.AddUShort(PlayerId);
            message.AddVector3(Position);
        }

        public void Deserialize(Message message)
        {
            PlayerId = message.GetUShort();
            Position = message.GetVector3();
        }
    }
}