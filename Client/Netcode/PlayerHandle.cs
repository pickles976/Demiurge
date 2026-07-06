

using System.Numerics;
using BepuPhysics.Trees;
using Riptide;
using Stride.Animations;
using Stride.CommunityToolkit.Engine;
using Stride.Engine;
using Stride.Engine.Events;

namespace Demiurge.GameClient
{
    internal class PlayerHandle
    {
        // TODO: better names! `PlayerList`, `NetworkId` etc
        internal static readonly Dictionary<ushort, PlayerHandle> List = new Dictionary<ushort, PlayerHandle>();

        internal static Game Game { get; set; }
        internal static Scene RootScene {get; set;}

        internal static EventReceiver<Vector3> InputEvent = new(GameEvents.PlayerInput);

        private Vector3 position;
        private readonly ushort id;

        private Entity playerEntity;

        internal PlayerHandle(ushort clientId, Vector3 position, Entity playerEntity)
        {
            id = clientId;
            this.position = position;
            this.playerEntity = playerEntity;
        }

        public static void Update(float dt)
        {
            foreach (PlayerHandle player in List.Values)
            {
                if (player.id == NetworkManager.ClientId)
                {
                    InputEvent.TryReceive(out Vector3 intent);
                    player.position += intent * PlayerData.Speed * dt;
                    player.SendPosition();
                    player.playerEntity.GetComponent<PlayerVisualScript>().SetPosition(player.position);
                }
            }
        }

        private void SendPosition()
        {
            Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.PlayerPosition);
            message.AddVector3(position);
            NetworkManager.Client.Send(message);
        }

        [MessageHandler((ushort)ServerToClientId.PlayerPosition)]
        private static void HandlePosition(Message message)
        {
            ushort id = message.GetUShort();
            Vector3 position = message.GetVector3();

            if (List.TryGetValue(id, out PlayerHandle player))
            {
                player.position = position;
                player.playerEntity.GetComponent<PlayerVisualScript>().SetPosition(position);
            }
        }

        [MessageHandler((ushort)ServerToClientId.PlayerSpawn)]
        private static void HandleSpawn(Message message)
        {
            ushort id = message.GetUShort();
            Vector3 position = message.GetVector3();
            List.Add(id, new PlayerHandle(id, position, CreatePlayer(position)));
        }

        static Entity CreatePlayer(Vector3 position)
        {

            // Add Animations
            var playerAnimations = new AnimationComponent();
            playerAnimations.Animations.Add("Walk", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Walk"));
            playerAnimations.Animations.Add("Idle", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Idle"));
            playerAnimations.Animations.Add("Aiming", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Aiming"));
            playerAnimations.Animations.Add("Crouch", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Crouch"));
            playerAnimations.Animations.Add("CrouchWalk", Game.Content.Load<AnimationClip>("models/cat_orange_anim_CrouchWalk"));

            // TODO: pull this out to the object registry and sync with messages
            Entity player = new Entity("CAT") {
                new ModelComponent(GLTFLoader.LoadModel(Game, "assets/models/cat_orange.gltf")),
                new PlayerVisualScript {},
                playerAnimations
            };
            player.Transform.Position = new Vector3(1.0f, 0.0f, 0);
            player.Scene = RootScene;

            return player;
        }

    }
}