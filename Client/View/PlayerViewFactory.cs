
using Demiurge;
using Demiurge.GameClient;
using Stride.Animations;
using Stride.Engine;

public class PlayerViewFactory
{
    private readonly Game game;
    private readonly Scene scene;

    public PlayerViewFactory(Game game, Scene scene, PlayerRegistry registry)
    {
        this.game = game;
        this.scene = scene;
        registry.PlayerJoined += CreatePlayerView;
    }

    private void CreatePlayerView(Player player)
    {
        var animations = new AnimationComponent();
            animations.Animations.Add("Walk", game.Content.Load<AnimationClip>("models/cat_orange_anim_Walk"));
            animations.Animations.Add("Idle", game.Content.Load<AnimationClip>("models/cat_orange_anim_Idle"));
            animations.Animations.Add("Aiming", game.Content.Load<AnimationClip>("models/cat_orange_anim_Aiming"));
            animations.Animations.Add("Crouch", game.Content.Load<AnimationClip>("models/cat_orange_anim_Crouch"));
            animations.Animations.Add("CrouchWalk", game.Content.Load<AnimationClip>("models/cat_orange_anim_CrouchWalk"));

        var entity = new Entity($"Player_{player.Id}")
        {
            new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/cat_orange.gltf")),
            new PlayerViewScript {Player = player},
            new PlayerVisualScript(),
            animations,
        };
        entity.Transform.Position = player.Position.ToStride();
        entity.Scene = scene;
    }
}