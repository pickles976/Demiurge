
using Demiurge;
using Demiurge.GameClient;
using Stride.Animations;
using Stride.Engine;
using Stride.Core.Mathematics;

public class PlayerViewFactory : IDisposable
{
    private readonly Game game;
    private readonly Scene scene;
    private readonly PlayerRegistry registry;

    public PlayerViewFactory(Game game, Scene scene, PlayerRegistry registry)
    {
        this.game = game;
        this.scene = scene;
        this.registry = registry;
        registry.PlayerJoined += CreatePlayerView;
        registry.PlayerLeft += DestroyPlayerView;
    }

    private void CreatePlayerView(Player player)
    {
        // Team decides the body. It arrives on the spawn packet, so it is already set when
        // PlayerJoined fires; a team CHANGE would need the view rebuilt, which nothing does today.
        var animations = new AnimationComponent();
        foreach (string clip in PlayerCosmetics.Clips)
            animations.Animations.Add(
                clip,
                game.Content.Load<AnimationClip>(PlayerCosmetics.AnimationPath(player.Team, clip)));

        var model = new ModelComponent(
            GLTFLoader.LoadModel(game, PlayerCosmetics.Model(player.Team)));
        if (player is LocalPlayer)
        {
            // First-person keeps the local player entity and skeleton alive for prediction,
            // animation, and equipped-item sockets, but does not render the full body around the eye.
            model.Enabled = false;
        }

        // Worn rather than modelled into the body, so one helmet serves every team and can be tuned
        // without touching a rig. The link makes the bone the parent, so its transform below is an
        // offset in BONE space; being a child of the body as well is what makes it go away with the
        // body when the view is destroyed.
        var helmetModel = new ModelComponent(GLTFLoader.LoadModel(game, PlayerCosmetics.HelmetModel));
        var helmet = new Entity($"Helmet_{player.Id}") { helmetModel };
        helmet.Add(new ModelNodeLinkComponent { Target = model, NodeName = PlayerCosmetics.HelmetBone });
        helmet.Transform.Position = PlayerCosmetics.HelmetSeat.ToStride();
        helmet.Transform.Rotation = PlayerCosmetics.HelmetRotation.ToStride();
        helmet.Transform.Scale = new Vector3(PlayerCosmetics.HelmetScale);

        var entity = new Entity($"Player_{player.Id}")
        {
            model,
            new PlayerViewScript { Player = player, Registry = registry, Helmet = helmetModel },
            animations,
        };
        entity.Transform.Position = player.Position.ToStride();
        entity.Transform.Children.Add(helmet.Transform);
        entity.Scene = scene;
    }

    private void DestroyPlayerView(Player player)
    {

        // Remove entity from the scene heirarchy
        if (scene.Entities.FirstOrDefault(e => e.Name == $"Player_{player.Id}") is {} playerEntity)
        {
            scene.Entities.Remove(playerEntity);
            playerEntity.Scene = null;
        }

    }

    public void Dispose()
    {
        registry.PlayerJoined -= CreatePlayerView;
        registry.PlayerLeft -= DestroyPlayerView;
        foreach (var entity in scene.Entities.Where(entity => entity.Name.StartsWith("Player_", StringComparison.Ordinal)).ToArray())
            entity.Scene = null;
    }

}
