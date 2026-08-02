using System.Numerics;
using Demiurge.Net;

namespace Demiurge
{
    public struct PlayerSpawnData : IMessageSerializable
    {
        public ushort PlayerId;
        public Vector3 Position;
        public int Team;

        public void Serialize(Message message)
        {
            message.AddUShort(PlayerId);
            message.AddVector3(Position);
            message.AddInt(Team);
        }

        public void Deserialize(Message message)
        {
            PlayerId = message.GetUShort();
            Position = message.GetVector3();
            Team = message.GetInt();
        }
    }
}
