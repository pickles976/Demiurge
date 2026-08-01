
using Demiurge;
using Demiurge.GameClient;
using Stride.CommunityToolkit.Bepu;
using Stride.CommunityToolkit.Engine;
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
    private readonly ModelLocators modelLocators;

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
        this.modelLocators = modelLocators;
        treeViews = new TreeViewFactory.Manager(game, scene, players, modelLocators);
        builders = new()
        {
            [ObjectType.Crate] = _ => game.Create3DPrimitive(PrimitiveModelType.Cube,
                                          new() { IncludeCollider = false }),
            [ObjectType.TrainingDummy] = _ => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/dummy.gltf")) },
            [ObjectType.Grenade] = _ => CreateThrownGrenade(),
            [ObjectType.Flag] = obj => new Entity
            {
                new FlagViewScript { Object = obj },
            },
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
            entity.Add(new ItemAttachScript { Object = obj, Mount = mount, Registry = players, CameraEntity = cameraEntity, WeaponView = weaponView, Locators = modelLocators, Priority = 25 });
        if (!isItem && obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new NetTransformScript { Object = obj });
        if (obj.Has.HasFlag(NetComponents.Health)) entity.Add(new HealthScaleScript { Object = obj });

        // A pickup on the ground and a worn item share one size; the first-person view model is the
        // only thing that draws an item at a different scale, and ItemAttachScript owns that.
        if (isItem)
            entity.Transform.Scale = new Stride.Core.Mathematics.Vector3(
                ItemCosmetics.WorldScale(obj.Item.Type));

        entity.Transform.Position = obj.Transform.Position.ToStride();
        entity.Scene = scene;
    }

    /// <summary>A grenade in flight. The model is a stick grenade about 0.32 m long; the 0.075 m
    /// GrenadeConfig.Radius stays what it always was — a server-side collision number, not a
    /// description of the art. The tumble runs after NetTransformScript and replaces its yaw.</summary>
    private Entity CreateThrownGrenade()
        => new()
        {
            new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Model(ItemType.Grenade))),
            new ThrownGrenadeSpinScript { Priority = 10 },
        };

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
