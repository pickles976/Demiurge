using System.Numerics;
using Riptide;

namespace Demiurge
{
    /// <summary>What kind of scenery/logic thing to build on spawn. Items are
    /// NOT here — they're all ObjectType.Item, and ItemState + the component
    /// mask say everything else. Only the view interprets this; the replication
    /// plumbing carries it opaquely. Append-only from here on.</summary>
    public enum ObjectType : ushort {
        Crate = 1,
        TrainingDummy,
        PlayerStatus,
        Item
    }

    /// <summary>Which item an ItemState describes — every pickup/wearable/weapon
    /// in the game, one enum. Wire protocol (rides inside ItemState) and the key
    /// into ItemConfig + ItemCosmetics — append-only.</summary>
    public enum ItemType : ushort
    {
        Ak47 = 1,
        AWP = 2,
        Glock = 3,
        BodyArmor = 4,
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
        Armor = 1 << 4,
        Item = 1 << 5
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

    /// <summary>Live weapon state only — WHICH gun lives in ItemState; static
    /// numbers live in ItemConfig, keyed by ItemState.Type, so they can't
    /// drift mid-match. Present only on items whose config has a weapon section.</summary>
    public struct WeaponState : IMessageSerializable
    {
        public int CurrentAmmo;
        public void Serialize(Message m) => m.AddInt(CurrentAmmo);
        public void Deserialize(Message m) => CurrentAmmo = m.GetInt();
    }

    /// <summary>Which player an object belongs to / is attached to. The view uses
    /// it to parent equipped weapons to the owner's hand.</summary>
    public struct OwnerState : IMessageSerializable
    {
        public ushort PlayerId;
        public void Serialize(Message m) => m.AddUShort(PlayerId);
        public void Deserialize(Message m) => PlayerId = m.GetUShort();
    }

    public struct ArmorState : IMessageSerializable
    {
        public float MaxValue;
        public float Current;

        public void Serialize (Message m) { m.AddFloat(MaxValue); m.AddFloat(Current);}
        public void Deserialize(Message m) {MaxValue = m.GetFloat(); Current = m.GetFloat(); }
    }

    /// <summary>What an object IS, when it's an item. The mask around it is the
    /// semantics: Item+Transform = pickup in the world, Item+Owner = equipped.
    /// Static data (slot, stats, model) is looked up by Type in ItemConfig /
    /// ItemCosmetics — never on the wire.</summary>
    public struct ItemState : IMessageSerializable
    {
        public ItemType Type;
        public void Serialize(Message m) => m.AddUShort((ushort)Type);
        public void Deserialize(Message m) => Type = (ItemType)m.GetUShort();
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
        public ArmorState Armor;
        public ItemState Item;

        public void Serialize(Message m)
        {
            m.AddUShort((ushort)Mask);
            if (Mask.HasFlag(NetComponents.Transform)) m.AddSerializable(Transform);
            if (Mask.HasFlag(NetComponents.Health)) m.AddSerializable(Health);
            if (Mask.HasFlag(NetComponents.Weapon)) m.AddSerializable(Weapon);
            if (Mask.HasFlag(NetComponents.Owner)) m.AddSerializable(Owner);
            if (Mask.HasFlag(NetComponents.Armor)) m.AddSerializable(Armor);
            if (Mask.HasFlag(NetComponents.Item)) m.AddSerializable(Item);
        }

        public void Deserialize(Message m)
        {
            Mask = (NetComponents)m.GetUShort();
            if (Mask.HasFlag(NetComponents.Transform)) Transform = m.GetSerializable<TransformState>();
            if (Mask.HasFlag(NetComponents.Health)) Health = m.GetSerializable<HealthState>();
            if (Mask.HasFlag(NetComponents.Weapon)) Weapon = m.GetSerializable<WeaponState>();
            if (Mask.HasFlag(NetComponents.Owner)) Owner = m.GetSerializable<OwnerState>();
            if (Mask.HasFlag(NetComponents.Armor)) Armor = m.GetSerializable<ArmorState>();
            if (Mask.HasFlag(NetComponents.Item)) Item = m.GetSerializable<ItemState>();
        }
    }
}
