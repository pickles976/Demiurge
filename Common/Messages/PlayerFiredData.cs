using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    /// <summary>Server → clients: a projectile the server accepted and spawned.
    /// Direction includes authoritative spread. Sent unreliable because this is a
    /// cosmetic mirror; damage is decided by the server projectile simulation.</summary>
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
