using System.Numerics;

namespace Demiurge.GameClient;

public interface IClientTerrainSource
{
    ChunkMap Map { get; }
    event Action<ChunkIndex>? ChunkCompleted;
    event Action<Vector3, Vector3>? RegionEdited;
    bool FootprintComplete(LodSection section);
}
