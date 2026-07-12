using System.Numerics;
using Riptide;
using Stride.Core.Diagnostics;

namespace Demiurge.GameClient
{
    public class NetworkManager
    {
        private static readonly Logger Log = GlobalLogger.GetLogger("Network");
        private readonly Client client = new();

        /// The id of this client that was assigned by the server during this session
        public ushort ClientId { get; private set; }

        // Netcode -> Simulation boundary
        public event Action<PlayerSpawnData>? PlayerSpawned;
        public event Action<PlayerDespawnData>? PlayerDespawned;
        public event Action<PlayerPositionData>? PlayerPositionReceived;

        public event Action<ObjectSpawnData>? ObjectSpawned;
        public event Action<ObjectDespawnData>? ObjectDespawned;
        public event Action<ObjectStateData>? ObjectStateReceived;




        public void Connect()
        {
            client.MessageReceived += OnMessageReceived;
            client.Connected += (_, _) => Log.Info("Connected to server");
            client.Connect($"127.0.0.1:{NetworkConfig.Port}", useMessageHandlers: false);
        }

        /// <summary>Pump once per frame from Program.cs Update().</summary>
        public void Update() => client.Update();

        // The ONLY place the rest of the client can send from.
        public void SendInput(PlayerInputData input)
        {
            Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.PlayerInput);
            message.AddSerializable(input);
            client.Send(message);
        }

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            switch ((ServerToClientId)e.MessageId)
            {
                case ServerToClientId.Welcome:
                    ClientId = e.Message.GetSerializable<WelcomeData>().ClientId;
                    break;
                case ServerToClientId.PlayerSpawn:
                    PlayerSpawned?.Invoke(e.Message.GetSerializable<PlayerSpawnData>());
                    break;
                case ServerToClientId.PlayerDespawn:
                    PlayerDespawned?.Invoke(e.Message.GetSerializable<PlayerDespawnData>());
                    break;
                case ServerToClientId.PlayerPosition:
                    PlayerPositionReceived?.Invoke(e.Message.GetSerializable<PlayerPositionData>());
                    break;
                case ServerToClientId.ObjectSpawn:
                    ObjectSpawned?.Invoke(e.Message.GetSerializable<ObjectSpawnData>());
                    break;
                case ServerToClientId.ObjectDespawn:
                    ObjectDespawned?.Invoke(e.Message.GetSerializable<ObjectDespawnData>());
                    break;
                case ServerToClientId.ObjectState:
                    ObjectStateReceived?.Invoke(e.Message.GetSerializable<ObjectStateData>());
                    break;
            }
        }
    }
}