using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    public struct ObjectDespawnData : IMessageSerializable
    {
        public uint NetworkId;

        /// <summary>
        /// Where the object was when it stopped existing.
        ///
        /// Carried because for anything that goes off, the despawn IS the event and its last
        /// broadcast transform is not where it happened: a mortar bomb closes 2 m per tick at its
        /// 60 m/s nominal speed, and a detonation is decided mid-tick, so the client's newest
        /// position for it is up to a bomb-length short of the crater it just dug. That is a third
        /// of the lethal radius, drawn in the air, next to the hole.
        ///
        /// A field on the despawn rather than a final transform update because the two would race —
        /// reliable delivery is unordered, and the object is gone from the broadcast set before the
        /// tick's dirty states go out, so the update would never be sent at all.
        /// </summary>
        public Vector3 Position;

        public void Serialize(Message message)
        {
            message.AddUInt(NetworkId);
            message.AddVector3(Position);
        }

        public void Deserialize(Message message)
        {
            NetworkId = message.GetUInt();
            Position = message.GetVector3();
        }
    }
}