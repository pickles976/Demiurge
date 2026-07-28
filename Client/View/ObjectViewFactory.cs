
using Demiurge;
using Demiurge.GameClient;
using Stride.CommunityToolkit.Bepu;
using Stride.CommunityToolkit.Rendering.ProceduralModels;
using Stride.Engine;

public class ObjectViewFactory : IDisposable
{
    private readonly Game game;
    private readonly Scene scene;
    private readonly WeaponMount mount;
    private readonly PlayerRegistry players;
    private readonly Entity cameraEntity;
    private readonly LocalWeaponView weaponView;
    private readonly TreeViewFactory.Manager treeViews;
    private readonly ObjectRegistry registry;

    // Scenery only. Items never appear here: their model comes from
    // ItemCosmetics and their behavior from the component mask.
    private readonly Dictionary<ObjectType, Func<NetObject, Entity>> builders;

    public ObjectViewFactory(Game game, Scene scene, ObjectRegistry registry, WeaponMount mount,
                             PlayerRegistry players, Entity cameraEntity, LocalWeaponView weaponView,
                             ModelLocators modelLocators)
    {
        this.registry = registry;
        this.game = game;
        this.scene = scene;
        this.mount = mount;
        this.players = players;
        this.cameraEntity = cameraEntity;
        this.weaponView = weaponView;
        treeViews = new TreeViewFactory.Manager(game, scene, players, modelLocators);
        builders = new()
        {
            [ObjectType.Crate] = _ => game.Create3DPrimitive(PrimitiveModelType.Cube,
                                          new() { IncludeCollider = false }),
            [ObjectType.TrainingDummy] = _ => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/dummy.gltf")) },
        };
        registry.ObjectSpawned += CreateView;
        registry.ObjectDespawned += DestroyView;
    }

    public void Dispose()
    {
        registry.ObjectSpawned -= CreateView;
        registry.ObjectDespawned -= DestroyView;
        foreach (var entity in scene.Entities.Where(entity => entity.Name.StartsWith("NetObject_", StringComparison.Ordinal)).ToArray())
            entity.Scene = null;
    }

    private void CreateView(NetObject obj)
    {
        bool isItem = obj.Has.HasFlag(NetComponents.Item);
        if (!isItem && obj.Type == ObjectType.Tree)
        {
            treeViews.Add(obj);
            return;
        }

        Entity entity;
        if (isItem)
            entity = new Entity { new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Model(obj.Item.Type))) };
        else if (builders.TryGetValue(obj.Type, out var build))
            entity = build(obj);
        else return;   // no visual (PlayerStatus, unknown types): skip, don't crash

        entity.Name = $"NetObject_{obj.NetworkId}";

        // View behavior per component the object HAS — the mask decides.
        // Item+Transform sits in the world: the bob presenter OWNS the entity
        // transform (so no NetTransformScript alongside). Item+Owner is worn:
        // the attach presenter owns it instead.
        if (isItem && obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new PickupBobScript { Object = obj });
        if (isItem && obj.Has.HasFlag(NetComponents.Owner))
            entity.Add(new ItemAttachScript { Object = obj, Mount = mount, Registry = players, CameraEntity = cameraEntity, WeaponView = weaponView, Priority = 15 });
        if (!isItem && obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new NetTransformScript { Object = obj });
        if (obj.Has.HasFlag(NetComponents.Health)) entity.Add(new HealthScaleScript { Object = obj });

        entity.Transform.Position = obj.Transform.Position.ToStride();
        entity.Scene = scene;
    }

    private void DestroyView(NetObject obj)
    {
        if (obj.Type == ObjectType.Tree)
        {
            treeViews.Remove(obj.NetworkId);
            return;
        }

        if (weaponView.NetworkId == obj.NetworkId) weaponView.Clear();

        if (scene.Entities.FirstOrDefault(e => e.Name == $"NetObject_{obj.NetworkId}") is { } entity)
        {
            scene.Entities.Remove(entity);
            entity.Scene = null;
        }
    }
}
