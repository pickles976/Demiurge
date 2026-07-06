using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct ClientPositionData : IMessageSerializable
    {
        public Vector3 Position;

        public void Serialize(Message message)
        {
            message.AddVector3(Position);
        }

        public void Deserialize(Message message)
        {
            Position = message.GetVector3();
        }
    }
}