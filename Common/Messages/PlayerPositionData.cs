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
        }

        /// <summary>The movement half of this message, as the shared step wants it.</summary>
        public MoveState Move => new() { Position = Position, Velocity = Velocity, Grounded = Grounded };
    }
}