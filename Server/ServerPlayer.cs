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

        public ServerObject? Status {get; set;}

        // What the player wears and holds: slot -> NetworkId of the equipped
        // item object. Live state (ammo) lives ON the objects; the player just
        // holds the references and the fire/reload timing gates below.
        public Dictionary<EquipSlot, uint> Equipped { get; } = new();

        public uint NextFireTick { get; set; }     // earliest tick the next shot is legal
        public uint ReloadDoneTick { get; set; }   // firing is blocked until this tick

        // Inputs arrive about once per-frame (unreliably) and are consumed once per tick, so they are queued.
        public Queue<PlayerInputData> PendingMoves {get; } = new();
        public uint LastReceivedSequence {get; set;} // newest enqueued
        public uint LastProcessedSequence {get; set;}
        public Vector3 LastIntent {get; set;} // reused when queue starves


        /// Where the player has been, 1s history
        public SnapshotBuffer History { get; } = new();

    }
}
