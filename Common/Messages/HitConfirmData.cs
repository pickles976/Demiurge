using Riptide;

namespace Demiurge
{
    public struct HitConfirmData : IMessageSerializable
    {
        public uint TargetNetworkId; // what was hit
        public ushort Damage;

        public void Serialize (Message message)
        {
            message.AddUInt(TargetNetworkId);
            message.AddUShort(Damage);
        }

        public void Deserialize(Message message)
        {
            TargetNetworkId = message.GetUInt();
            Damage = message.GetUShort();
        }
    }
}