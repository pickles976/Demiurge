

using System.Numerics;
using BepuPhysics.Trees;
using Riptide;
using Stride.Animations;
using Stride.CommunityToolkit.Engine;
using Stride.Engine;

namespace Demiurge.GameClient
{
    internal class PlayerHandle
    {
        // TODO: better names! `PlayerList`, `NetworkId` etc
        internal static readonly Dictionary<ushort, PlayerHandle> List = new Dictionary<ushort, PlayerHandle>();

        internal static Game Game { get; set; }
        internal static Scene RootScene {get; set;}

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
                var inputScript = player.playerEntity.GetComponent<PlayerInputScript>();
                if (inputScript == null) return;
                Vector3 intent = inputScript.intent;
                player.position += intent * PlayerData.Speed * dt;
                player.SendPosition();
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
            }
        }

        [MessageHandler((ushort)ServerToClientId.PlayerSpawn)]
        private static void HandleSpawn(Message message)
        {
            ushort id = message.GetUShort();
            Vector3 position = message.GetVector3();

            if (id == NetworkManager.ClientId)
            {
                List.Add(id, new PlayerHandle(id, position, CreatePlayer(true, position)));
            }
            // TODO: fix this why does it delete our player input script?
            // else
            // {
            //     List.Add(id, new PlayerHandle(id, position, CreatePlayer(false, position)));
            // }
        }

        static Entity CreatePlayer(bool isLocalPlayer, Vector3 position)
        {

            // Add Animations
            var playerAnimations = new AnimationComponent();
            playerAnimations.Animations.Add("Walk", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Walk"));
            playerAnimations.Animations.Add("Idle", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Idle"));
            playerAnimations.Animations.Add("Aiming", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Aiming"));
            playerAnimations.Animations.Add("Crouch", Game.Content.Load<AnimationClip>("models/cat_orange_anim_Crouch"));
            playerAnimations.Animations.Add("CrouchWalk", Game.Content.Load<AnimationClip>("models/cat_orange_anim_CrouchWalk"));

            Entity player;

            // TODO: pull this out to the object registry and sync with messages
            if (isLocalPlayer)
            {
                var cameraEntity = Game.Add3DCamera();
                LineRenderer.Camera = cameraEntity.Get<CameraComponent>();

                player = new Entity("CAT") {
                    new ModelComponent(GLTFLoader.LoadModel(Game, "assets/models/cat_orange.gltf")),
                    new PlayerInputScript {CameraEntity = cameraEntity},
                    new PlayerVisualScript {},
                    playerAnimations
                };

                cameraEntity.Add(new ThirdPersonCameraScript { PlayerEntity = player });
                cameraEntity.Add(new CursorReticleScript());

            }
            else
            {
                player = new Entity("CAT") {
                    new ModelComponent(GLTFLoader.LoadModel(Game, "assets/models/cat_orange.gltf")),
                    new PlayerVisualScript {},
                    playerAnimations
                };
            }

            player.Transform.Position = new Vector3(1.0f, 0.0f, 0);
            player.Scene = RootScene;


            return player;
        }

    }
}