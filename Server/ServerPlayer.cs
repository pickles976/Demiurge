using System.Numerics;

namespace Demiurge.GameServer
{
    public class ServerPlayer
    {
        public ushort Id { get; init; }
        public Vector3 Position { get; set; }
        public PlayerStateFlags State { get; set; }
        public Vector3 PendingIntent { get; set; }
        public float Yaw {get; set;}
    }
}
