
using Demiurge;
using Demiurge.GameClient;
using Stride.CommunityToolkit.Bepu;
using Stride.CommunityToolkit.Rendering.ProceduralModels;
using Stride.Engine;

public class ObjectViewFactory
{
    private readonly Game game;
    private readonly Scene scene;
    private readonly Dictionary<ObjectType, Func<NetObject, Entity>> builders;

    public ObjectViewFactory(Game game, Scene scene, ObjectRegistry registry)
    {
        this.game = game;
        this.scene = scene;
        builders = new()
        {
            [ObjectType.Crate] = _ => game.Create3DPrimitive(PrimitiveModelType.Cube,
                                          new() { IncludeCollider = false }),
            [ObjectType.TrainingDummy] = _ => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/dummy.gltf")) },
            [ObjectType.WeaponPickup] = obj => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, WeaponCosmetics.Get(obj.Weapon.Type).ModelPath)),
                  new PickupBobScript { Object = obj } },
            [ObjectType.EquippedWeapon] = obj => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, WeaponCosmetics.Get(obj.Weapon.Type).ModelPath)),
                  new WeaponAttachScript { Object = obj } },
            [ObjectType.ArmorPickup] = obj => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/body_armor.gltf")),
                  new PickupBobScript { Object = obj } },
        };
        registry.ObjectSpawned += CreateView;
        registry.ObjectDespawned += DestroyView;
    }

    private void CreateView(NetObject obj)
    {
        if (!builders.TryGetValue(obj.Type, out var build)) return; // unknown type: skip, don't crash

        var entity = build(obj);
        entity.Name = $"NetObject_{obj.NetworkId}";

        // Attach view behavior per component the object HAS — the mask decides,
        // not the type. A new type with Health gets a health view for free.
        // Exception: a builder may install its own transform presenter (the
        // pickup's bob/spin), which then owns the entity transform instead.
        bool customTransformView = entity.Get<PickupBobScript>() != null;
        if (obj.Has.HasFlag(NetComponents.Transform) && !customTransformView)
            entity.Add(new NetTransformScript { Object = obj });
        if (obj.Has.HasFlag(NetComponents.Health)) entity.Add(new HealthScaleScript { Object = obj });

        entity.Transform.Position = obj.Transform.Position.ToStride();
        entity.Scene = scene;
    }

    private void DestroyView(NetObject obj)
    {
        if (scene.Entities.FirstOrDefault(e => e.Name == $"NetObject_{obj.NetworkId}") is { } entity)
        {
            scene.Entities.Remove(entity);
            entity.Scene = null;
        }
    }
}
