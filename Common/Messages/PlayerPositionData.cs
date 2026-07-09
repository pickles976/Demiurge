using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct PlayerPositionData : IMessageSerializable
    {
        public ushort PlayerId;
        public uint Tick;
        public Vector3 Position;

        public PlayerStateFlags State;

        public float Yaw;

        // Only used by the player that generated the input
        public uint LastProcessedSequence;

        public void Serialize(Message message)
        {
            message.AddUShort(PlayerId);
            message.AddUInt(Tick);
            message.AddVector3(Position);
            message.AddUShort((ushort)State);
            message.AddFloat(Yaw);
            message.AddUInt(LastProcessedSequence);
        }

        public void Deserialize(Message message)
        {
            PlayerId = message.GetUShort();
            Tick = message.GetUInt();
            Position = message.GetVector3();
            State = (PlayerStateFlags)message.GetUShort();
            Yaw = message.GetFloat();
            LastProcessedSequence = message.GetUInt();
        }
    }
}