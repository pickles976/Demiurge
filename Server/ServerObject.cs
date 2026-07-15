namespace Demiurge.GameServer
{
    public class ServerObject
    {
        public uint NetworkId {get; init;}
        public ObjectType Type {get; init;}
        public NetComponents Has {get; init;}

        // Fields, not properties: mutable structs accessed through a property getter
        // return a copy, so `obj.Transform.Position = x` would edit the copy, not the object

        public NetComponents Dirty;
        public TransformState Transform;
        public HealthState Health;
        public WeaponState Weapon;
        public OwnerState Owner;
        public ArmorState Armor;
        public ItemState Item;
        public AttachmentState Attachment;

        /// <summary>THE one place component state moves between server object
        /// instances (ItemSystem's equip/drop transitions). New component = one
        /// new line here — the same checklist as ObjectRegistry.CopyComponents
        /// on the client. Forgetting a line silently zeroes live state on swap.</summary>
        public static void CopyComponents(ServerObject src, ServerObject dst, NetComponents mask)
        {
            if (mask.HasFlag(NetComponents.Transform)) dst.Transform = src.Transform;
            if (mask.HasFlag(NetComponents.Health)) dst.Health = src.Health;
            if (mask.HasFlag(NetComponents.Weapon)) dst.Weapon = src.Weapon;
            if (mask.HasFlag(NetComponents.Owner)) dst.Owner = src.Owner;
            if (mask.HasFlag(NetComponents.Armor)) dst.Armor = src.Armor;
            if (mask.HasFlag(NetComponents.Item)) dst.Item = src.Item;
            if (mask.HasFlag(NetComponents.Attachment)) dst.Attachment = src.Attachment;
        }
    }
}
