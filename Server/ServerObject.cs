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
    }
}