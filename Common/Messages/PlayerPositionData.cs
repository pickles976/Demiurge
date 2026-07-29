using System.Numerics;
using Riptide;

namespace Demiurge
{
    public struct PlayerPositionData : IMessageSerializable
    {
        public ushort PlayerId;
        public uint Tick;
        public Vector3 Position;

        public PlayerStateFlags State;

        public float Yaw;

        // Only used by the player that generated the input
        public uint LastProcessedSequence;

        // The rest of the authoritative movement state. Position alone is not enough to replay from:
        // reconciliation re-steps pending moves, and a replay that starts from the server's position
        // but the client's velocity diverges on the first tick after every correction.
        public Vector3 Velocity;
        public bool Grounded;

        /// <summary>Look angle above the horizon, radians, positive is up. Replicated because a
        /// remote player's head and gun aim with it. APPENDED — never inserted.</summary>
        public float Pitch;

        /// <summary>Selected hotbar position, used to show the correct remote held item. APPENDED.</summary>
        public HotbarSlot Hotbar;

        /// <summary>
        /// Zero while alive; otherwise the authoritative global wave tick assigned on death.
        /// APPENDED — the client uses this for its respawn countdown.
        /// </summary>
        public uint RespawnTick;

        public void Serialize(Message message)
        {
            message.AddUShort(PlayerId);
            message.AddUInt(Tick);
            message.AddVector3(Position);
            message.AddUShort((ushort)State);
            message.AddFloat(Yaw);
            message.AddUInt(LastProcessedSequence);
            message.AddVector3(Velocity);
            message.AddBool(Grounded);
            message.AddFloat(Pitch);
            message.AddByte((byte)Hotbar);
            message.AddUInt(RespawnTick);
        }

        public void Deserialize(Message message)
        {
            PlayerId = message.GetUShort();
            Tick = message.GetUInt();
            Position = message.GetVector3();
            State = (PlayerStateFlags)message.GetUShort();
            Yaw = message.GetFloat();
            LastProcessedSequence = message.GetUInt();
            Velocity = message.GetVector3();
            Grounded = message.GetBool();
            Pitch = message.GetFloat();
            Hotbar = (HotbarSlot)message.GetByte();
            RespawnTick = message.GetUInt();
        }

        /// <summary>The movement half of this message, as the shared step wants it.</summary>
        public MoveState Move => new() { Position = Position, Velocity = Velocity, Grounded = Grounded };
    }
}
