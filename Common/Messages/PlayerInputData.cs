using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    public struct PlayerInputData : IMessageSerializable
    {
        public uint Sequence;   // unused until prediction reconciliation
        public Vector3 Intent;
        public PlayerStateFlags State;

        public float Yaw;

        /// <summary>Look angle above the horizon, radians, positive is up. APPENDED to this message,
        /// never inserted — the field order IS the protocol.</summary>
        public float Pitch;

        /// <summary>Currently selected numbered hotbar position. APPENDED.</summary>
        public HotbarSlot Hotbar;

        public void Serialize(Message message)
        {
            message.AddUInt(Sequence);
            message.AddVector3(Intent);
            message.AddUShort((ushort)State);
            message.AddFloat(Yaw);
            message.AddFloat(Pitch);
            message.AddByte((byte)Hotbar);
        }

        public void Deserialize(Message message)
        {
            Sequence = message.GetUInt();
            Intent = message.GetVector3();
            State = (PlayerStateFlags)message.GetUShort();
            Yaw = message.GetFloat();
            Pitch = message.GetFloat();
            Hotbar = (HotbarSlot)message.GetByte();
        }
    }
}
