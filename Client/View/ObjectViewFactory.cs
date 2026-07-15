
using Demiurge;
using Demiurge.GameClient;
using Stride.CommunityToolkit.Bepu;
using Stride.CommunityToolkit.Rendering.ProceduralModels;
using Stride.Engine;

public class ObjectViewFactory
{
    private readonly Game game;
    private readonly Scene scene;

    // Scenery only. Items never appear here: their model comes from
    // ItemCosmetics and their behavior from the component mask.
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
        };
        registry.ObjectSpawned += CreateView;
        registry.ObjectDespawned += DestroyView;
    }

    private void CreateView(NetObject obj)
    {
        bool isItem = obj.Has.HasFlag(NetComponents.Item);

        Entity entity;
        if (isItem)
            entity = new Entity { new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Get(obj.Item.Type).ModelPath)) };
        else if (builders.TryGetValue(obj.Type, out var build))
            entity = build(obj);
        else return;   // no visual (PlayerStatus, unknown types): skip, don't crash

        entity.Name = $"NetObject_{obj.NetworkId}";

        // View behavior per component the object HAS — the mask decides.
        // Item+Transform sits in the world: the bob presenter OWNS the entity
        // transform (so no NetTransformScript alongside). Item+Owner is worn:
        // the attach presenter owns it instead.
        if (isItem && obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new PickupBobScript { Object = obj });
        if (isItem && obj.Has.HasFlag(NetComponents.Owner)) entity.Add(new ItemAttachScript { Object = obj });
        if (!isItem && obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new NetTransformScript { Object = obj });
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
