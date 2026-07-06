using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct PlayerPositionData : IMessageSerializable
    {
        public ushort PlayerId;
        public Vector3 Position;

        public float Yaw;

        public void Serialize(Message message)
        {
            message.AddUShort(PlayerId);
            message.AddVector3(Position);
            message.AddFloat(Yaw);
        }

        public void Deserialize(Message message)
        {
            PlayerId = message.GetUShort();
            Position = message.GetVector3();
            Yaw = message.GetFloat();
        }
    }
}