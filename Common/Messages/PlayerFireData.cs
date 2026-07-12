using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct PlayerFireData: IMessageSerializable
    {
        public uint Sequence; // position in the input stream -- for lag compensation
        public Vector3 Origin;
        public Vector3 Direction;

        public void Serialize(Message message)
        {
            message.AddUInt(Sequence);
            message.AddVector3(Origin);
            message.AddVector3(Direction);
        }

        public void Deserialize(Message message)
        {
            Sequence = message.GetUInt();
            Origin = message.GetVector3();
            Direction = message.GetVector3();
        }
    }
}