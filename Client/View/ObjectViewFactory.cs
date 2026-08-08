
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

    // Keyed by ObjectType, and it WINS over the item model below: an object whose type names its
    // own view gets it even when it also carries an Item. That is how a supply crate works — the
    // crate is the view, the item inside is what picking it up gives you. Every ordinary item
    // spawns as ObjectType.Item, has no entry here, and still gets its model from ItemCosmetics.
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
            [ObjectType.Crate] = _ => new Entity
            {
                new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.SupplyCrateModel)),
            },
            [ObjectType.TrainingDummy] = _ => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/dummy.gltf")) },
            [ObjectType.Grenade] = _ => CreateThrownGrenade(),
            [ObjectType.MortarRound] = _ => CreateMortarRound(),
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

        // The type's own view first, so a crated pickup draws as its crate.
        bool typedView = builders.TryGetValue(obj.Type, out var build);

        Entity entity;
        if (typedView)
            entity = build!(obj);
        else if (isItem)
            entity = new Entity { new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Model(obj.Item.Type))) };
        else return;   // no visual (PlayerStatus, unknown types): skip, don't crash

        entity.Name = $"NetObject_{obj.NetworkId}";

        // View behavior per component the object HAS — the mask decides.
        // Item+Transform sits in the world: the bob presenter OWNS the entity
        // transform (so no NetTransformScript alongside). Item+Owner is worn:
        // the attach presenter owns it instead. A crated pickup is the exception
        // to the hover-and-spin: a supply crate rests where the server put it.
        // A carryable joins the supply crate as an exception to the hover-and-spin: it was SET
        // DOWN somewhere, deliberately, facing a particular way, and a pickup that turns on the spot
        // would be lying about the one fact that matters most about it. NetTransformScript takes it
        // instead, which applies the replicated yaw — the heading it was emplaced on.
        bool bobs = isItem && !typedView && obj.Has.HasFlag(NetComponents.Transform)
                    && !ItemConfig.IsCarryable(obj.Item.Type);
        if (bobs) entity.Add(new PickupBobScript { Object = obj });
        if (isItem && obj.Has.HasFlag(NetComponents.Owner))
            entity.Add(new ItemAttachScript { Object = obj, Mount = mount, Registry = players, CameraEntity = cameraEntity, WeaponView = weaponView, Locators = modelLocators, Priority = 25 });
        if (!bobs && obj.Has.HasFlag(NetComponents.Transform)
            && !obj.Has.HasFlag(NetComponents.Owner))
            entity.Add(new NetTransformScript { Object = obj });
        if (obj.Has.HasFlag(NetComponents.Health)) entity.Add(new HealthScaleScript { Object = obj });

        // A pickup on the ground and a worn item share one size; the first-person view model is the
        // only thing that draws an item at a different scale, and ItemAttachScript owns that. A
        // typed view is drawn at the scale its own model was authored at.
        if (isItem && !typedView)
            entity.Transform.Scale = new Stride.Core.Mathematics.Vector3(
                ItemCosmetics.WorldScale(obj.Item.Type));

        entity.Transform.Position = obj.Transform.Position.ToStride();
        entity.Scene = scene;
    }

    /// <summary>A grenade in flight. The model is a stick grenade about 0.32 m long; the 0.075 m
    /// GrenadeConfig.Radius stays what it always was — a server-side collision number, not a
    /// description of the art. The tumble runs after NetTransformScript and replaces its yaw.</summary>
    /// <summary>
    /// A bomb in the air. The grenade's model for now — it is a small dark object at three hundred
    /// metres — with the trail that actually makes it findable in the sky.
    /// </summary>
    private Entity CreateMortarRound()
        => new()
        {
            new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Model(ItemType.Grenade))),
            new MortarRoundScript { Priority = 10 },
        };

    private Entity CreateThrownGrenade()
        => new()
        {
            new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Model(ItemType.Grenade))),
            new ThrownGrenadeScript { Priority = 10 },
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
