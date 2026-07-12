using System.Numerics;
using Riptide;

namespace Demiurge
{
    /// What kind of thing to build on spawn. Only the view layer interprets /// this — the replication plumbing carries it opaquely.
    public enum ObjectType : ushort { Crate = 1, TrainingDummy, }

    /// <summary>One bit per replicated component. Doubles as "what an object HAS"
    /// (spawn) and "what changed" (update). Append-only: these bits are wire protocol.</summary>

    [Flags]
    public enum NetComponents : ushort
    {
        None = 0,
        Transform = 1 << 0,
        Health = 1 << 1,
    }

    public struct TransformState : IMessageSerializable
    {
        public Vector3 Position;
        public float Yaw;
        public void Serialize (Message m ) {m.AddVector3(Position); m.AddFloat(Yaw);}
        public void Deserialize(Message m) {Position = m.GetVector3(); Yaw = m.GetFloat();}
    }

    public struct HealthState : IMessageSerializable
    {
        public ushort Current;
        public ushort Max;
        public void Serialize(Message m) {m.AddUShort(Current); m.AddUShort(Max);}
        public void Deserialize(Message m) {Current = m.GetUShort(); Max = m.GetUShort();}

    }

    /// <summary>Some subset of an object's components, mask-prefixed. The if-chain
    /// order is the wire format; new components go at the end of both methods.</summary>
    public struct ComponentBundle : IMessageSerializable
    {
        public NetComponents Mask;
        public TransformState Transform;
        public HealthState Health;

        public void Serialize(Message m)
        {
            m.AddUShort((ushort)Mask);
            if (Mask.HasFlag(NetComponents.Transform)) m.AddSerializable(Transform);
            if (Mask.HasFlag(NetComponents.Health)) m.AddSerializable(Health);
        }

        public void Deserialize(Message m)
        {
            Mask = (NetComponents)m.GetUShort();
            if (Mask.HasFlag(NetComponents.Transform)) Transform = m.GetSerializable<TransformState>();
            if (Mask.HasFlag(NetComponents.Health)) Health = m.GetSerializable<HealthState>();
        }
    }
}