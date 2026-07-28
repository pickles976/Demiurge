using System.Numerics;

namespace Demiurge.GameServer;

internal interface ICommandWorld
{
    ServerPlayer SpawnMob(Vector3? requestedPosition = null);
    ServerObject SpawnPickup(ItemType type, Vector3 position);
    bool TryGetActor(ushort actorId, out ServerPlayer actor);
    ServerObject Equip(ServerPlayer actor, ItemType type);
    bool IsSpawnableColumn(float worldX, float worldZ);
    Vector3 SurfacePosition(float worldX, float worldZ);
}
