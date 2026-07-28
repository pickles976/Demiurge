using System.Numerics;
using Riptide;

namespace Demiurge
{
    /// <summary>Server → clients: a shot the server ACCEPTED, for remote tracer
    /// and audio only. Sent unreliable — a lost tracer is cosmetic, and damage
    /// was already applied authoritatively. Carries the weapon type so effects
    /// don't have to race the equipped-object lookup.</summary>
    public struct PlayerFiredData : IMessageSerializable
    {
        public ushort PlayerId;
        public ItemType Weapon;
        public Vector3 Origin;
        public Vector3 Direction;

        public void Serialize(Message message)
        {
            message.AddUShort(PlayerId);
            message.AddUShort((ushort)Weapon);
            message.AddVector3(Origin);
            message.AddVector3(Direction);
        }

        public void Deserialize(Message message)
        {
            PlayerId = message.GetUShort();
            Weapon = (ItemType)message.GetUShort();
            Origin = message.GetVector3();
            Direction = message.GetVector3();
        }
    }
}
