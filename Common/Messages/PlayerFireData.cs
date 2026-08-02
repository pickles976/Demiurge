using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    public struct PlayerFireData: IMessageSerializable
    {
        public uint Sequence; // position in the input stream -- for lag compensation
        public Vector3 Origin;
        /// <summary>Centre of the requested aim cone; the server applies authoritative spread.</summary>
        public Vector3 Direction;
        public float RenderTick;
        /// <summary>Selected slot at the instant of firing. APPENDED so a same-frame switch works.</summary>
        public HotbarSlot Hotbar;

        public void Serialize(Message message)
        {
            message.AddUInt(Sequence);
            message.AddVector3(Origin);
            message.AddVector3(Direction);
            message.AddFloat(RenderTick);
            message.AddByte((byte)Hotbar);
        }

        public void Deserialize(Message message)
        {
            Sequence = message.GetUInt();
            Origin = message.GetVector3();
            Direction = message.GetVector3();
            RenderTick = message.GetFloat();
            Hotbar = (HotbarSlot)message.GetByte();
        }
    }
}
