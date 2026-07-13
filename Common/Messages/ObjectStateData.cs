using System.Runtime.InteropServices;
using Riptide;

namespace Demiurge
{
    
    public struct ObjectStateData: IMessageSerializable
    {
        public uint NetworkId;
        public uint Tick; // for interpolation
        public ComponentBundle State; // Mask

        public void Serialize(Message message)
        {
            message.AddUInt(NetworkId);
            message.AddUInt(Tick);
            message.AddSerializable(State);
        }

        public void Deserialize(Message message)
        {
            NetworkId = message.GetUInt();
            Tick = message.GetUInt();
            State = message.GetSerializable<ComponentBundle>();
        }
    }

}