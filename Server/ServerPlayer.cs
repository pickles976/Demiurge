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

        // Inputs arrive about once per-frame (unreliably) and are consumed once per tick, so they are queued.
        public Queue<PlayerInputData> PendingMoves {get; } = new();
        public uint LastReceivedSequence {get; set;} // newest enqueued
        public uint LastProcessedSequence {get; set;}
        public Vector3 LastIntent {get; set;} // reused when queue starves
    }
}
