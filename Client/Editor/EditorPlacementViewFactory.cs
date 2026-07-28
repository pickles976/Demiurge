using Demiurge.Editor;
using Demiurge.GameClient;
using Stride.Engine;

namespace Demiurge;

public sealed class EditorPlacementViewFactory : IDisposable
{
    private readonly Game game;
    private readonly Scene scene;
    private readonly EditorSession session;
    private readonly Dictionary<Guid, Entity> views = [];

    public EditorPlacementViewFactory(Game game, Scene scene, EditorSession session)
    {
        this.game = game;
        this.scene = scene;
        this.session = session;
        session.Changed += OnChanged;
        RefreshAll();
    }

    public void Dispose()
    {
        session.Changed -= OnChanged;
        foreach (var entity in views.Values) entity.Scene = null;
        views.Clear();
    }

    private void OnChanged(EditorChange change)
    {
        if (change.PlacementIds.Count > 0) RefreshAll();
    }

    private void RefreshAll()
    {
        var live = session.Document.Placements.Select(placement => placement.Id).ToHashSet();
        foreach (var id in views.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            views[id].Scene = null;
            views.Remove(id);
        }

        foreach (var placement in session.Document.Placements)
        {
            if (!views.TryGetValue(placement.Id, out var entity))
            {
                entity = Create(placement);
                views.Add(placement.Id, entity);
                entity.Scene = scene;
            }
            entity.Transform.Position = new Stride.Core.Mathematics.Vector3(
                placement.Cell.X + 0.5f, placement.Cell.Y, placement.Cell.Z + 0.5f);
            entity.Transform.Rotation = Stride.Core.Mathematics.Quaternion.RotationY(placement.Yaw);
        }
    }

    private Entity Create(EditorPlacement placement)
    {
        string? modelPath = placement.Kind switch
        {
            EditorPlacementKind.Pickup when ItemCatalog.TryResolve(placement.ArchetypeId, out var item)
                => ItemCosmetics.Model(item),
            EditorPlacementKind.Mob => "assets/models/dummy.gltf",
            _ => null,
        };

        return modelPath is null
            ? new Entity($"EditorPlacement_{placement.Id}")
            : new Entity($"EditorPlacement_{placement.Id}")
            {
                new ModelComponent(GLTFLoader.LoadModel(game, modelPath)),
            };
    }
}
