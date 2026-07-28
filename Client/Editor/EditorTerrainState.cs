using System.Numerics;
using Demiurge.Editor;
using Demiurge.GameClient;

namespace Demiurge;

public sealed class EditorTerrainState : IClientTerrainSource, IDisposable
{
    private readonly EditorSession session;

    public ChunkMap Map => session.Terrain;
    public event Action<ChunkIndex>? ChunkCompleted;
    public event Action<Vector3, Vector3>? RegionEdited;

    public EditorTerrainState(EditorSession session)
    {
        this.session = session;
        session.Changed += OnChanged;
    }

    public void AnnounceAll()
    {
        foreach (var chunk in Map.Snapshot())
            ChunkCompleted?.Invoke(chunk.index);
    }

    public bool FootprintComplete(LodSection section)
    {
        var (min, max) = section.ChunkFootprint();
        for (int z = min.z; z <= max.z; z++)
            for (int x = min.x; x <= max.x; x++)
                if (!Map.Has(new ChunkIndex { x = x, z = z })) return false;
        return true;
    }

    public void Dispose() => session.Changed -= OnChanged;

    private void OnChanged(EditorChange change)
    {
        foreach (var chunk in change.TerrainChunks)
        {
            (int x, int z) = ChunkTransforms.ChunkOrigin(chunk);
            RegionEdited?.Invoke(
                new Vector3(x, ChunkConstants.WorldMinY, z),
                new Vector3(
                    x + ChunkConstants.ChunkWidth - 1,
                    ChunkConstants.WorldMaxY - 1,
                    z + ChunkConstants.ChunkWidth - 1));
        }
    }
}
