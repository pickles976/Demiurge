using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct PlayerInputData : IMessageSerializable
    {
        public uint Sequence;   // unused until prediction reconciliation
        public Vector3 Intent;
        public PlayerStateFlags State;

        public float Yaw;

        public void Serialize(Message message)
        {
            message.AddUInt(Sequence);
            message.AddVector3(Intent);
            message.AddUShort((ushort)State);
            message.AddFloat(Yaw);
        }

        public void Deserialize(Message message)
        {
            Sequence = message.GetUInt();
            Intent = message.GetVector3();
            State = (PlayerStateFlags)message.GetUShort();
            Yaw = message.GetFloat();
        }
    }
}
