using System.Numerics;
using Riptide;

namespace Demiurge
{
    /// What kind of thing to build on spawn. Only the view layer interprets /// this — the replication plumbing carries it opaquely.
    public enum ObjectType : ushort {
        Crate = 1,
        TrainingDummy,
        WeaponPickup,
        EquippedWeapon,
        PlayerStatus
    }

    /// <summary>Which gun a WeaponState describes. Wire protocol (rides inside
    /// WeaponState) and the key into WeaponConfig — append-only.</summary>
    public enum WeaponType : ushort
    {
        Ak47 = 1,
    }

    /// <summary>One bit per replicated component. Doubles as "what an object HAS"
    /// (spawn) and "what changed" (update). Append-only: these bits are wire protocol.</summary>

    [Flags]
    public enum NetComponents : ushort
    {
        None = 0,
        Transform = 1 << 0,
        Health = 1 << 1,
        Weapon = 1 << 2,
        Owner = 1 << 3,
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

    /// <summary>Live weapon state only. The static numbers (capacity, cadence,
    /// damage...) are NOT on the wire — both ends look them up in WeaponConfig
    /// by Type, so they can't drift mid-match.</summary>
    public struct WeaponState : IMessageSerializable
    {
        public WeaponType Type;
        public int CurrentAmmo;
        public void Serialize(Message m) {m.AddUShort((ushort)Type); m.AddInt(CurrentAmmo);}
        public void Deserialize(Message m) {Type = (WeaponType)m.GetUShort(); CurrentAmmo = m.GetInt();}
    }

    /// <summary>Which player an object belongs to / is attached to. The view uses
    /// it to parent equipped weapons to the owner's hand.</summary>
    public struct OwnerState : IMessageSerializable
    {
        public ushort PlayerId;
        public void Serialize(Message m) => m.AddUShort(PlayerId);
        public void Deserialize(Message m) => PlayerId = m.GetUShort();
    }

    /// <summary>Some subset of an object's components, mask-prefixed. The if-chain
    /// order is the wire format; new components go at the end of both methods.</summary>
    public struct ComponentBundle : IMessageSerializable
    {
        public NetComponents Mask;
        public TransformState Transform;
        public HealthState Health;
        public WeaponState Weapon;
        public OwnerState Owner;

        public void Serialize(Message m)
        {
            m.AddUShort((ushort)Mask);
            if (Mask.HasFlag(NetComponents.Transform)) m.AddSerializable(Transform);
            if (Mask.HasFlag(NetComponents.Health)) m.AddSerializable(Health);
            if (Mask.HasFlag(NetComponents.Weapon)) m.AddSerializable(Weapon);
            if (Mask.HasFlag(NetComponents.Owner)) m.AddSerializable(Owner);
        }

        public void Deserialize(Message m)
        {
            Mask = (NetComponents)m.GetUShort();
            if (Mask.HasFlag(NetComponents.Transform)) Transform = m.GetSerializable<TransformState>();
            if (Mask.HasFlag(NetComponents.Health)) Health = m.GetSerializable<HealthState>();
            if (Mask.HasFlag(NetComponents.Weapon)) Weapon = m.GetSerializable<WeaponState>();
            if (Mask.HasFlag(NetComponents.Owner)) Owner = m.GetSerializable<OwnerState>();
        }
    }
}
