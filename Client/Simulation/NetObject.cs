using Demiurge;

// Sim-layer mirror of a server object. Netcode writes, view reads — same
// contract as Player. Has says which component fields are meaningful.
public class NetObject
{
    public uint NetworkId { get; init; }
    public ObjectType Type { get; init; }
    public NetComponents Has { get; init; }

    public TransformState Transform;   // fields: mutable structs (see ServerObject)
    public HealthState Health;
    public WeaponState Weapon;
    public OwnerState Owner;
    public ArmorState Armor;
    public ItemState Item;
    
    public SnapshotBuffer Snapshots { get; } = new();
}
