using System.Numerics;
using Riptide;
using Stride.Core.Diagnostics;

namespace Demiurge.GameClient
{
    public class NetworkManager
    {

        private readonly PriorityQueue<Action, double> delayed = new();
        private readonly Random rng = new();
        private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;


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

        /// <summary>
        /// Carries the token for the separate terrain stream, so the composition root can connect it.
        /// Terrain itself does NOT come through here any more — see <see cref="ChunkTransport"/>.
        /// </summary>
        public event Action<WelcomeData>? Welcomed;

        public event Action<PlayerFiredData>? PlayerFired;   // cosmetic: remote shot FX

        /// <summary>An edit the server has already made. Raised on the NETWORK thread when fake
        /// latency is off, so the handler must only queue — see TerrainState.ReceiveEdit.</summary>
        public event Action<TerrainEditData>? TerrainEdited;
        public event Action<HitConfirmData>? HitConfirmed;   // cosmetic: your shot landed
        public event Action<CommandResultData>? CommandResultReceived;

        private uint nextCommandRequestId;

        private void Dispatch(Action deliver)
        {
            // Call immediately
            if (NetworkConfig.SimulatedLatencySeconds <= 0f) { deliver(); return; }

            double due = Now + NetworkConfig.SimulatedLatencySeconds
                + (rng.NextDouble() * 2.0 - 1.0) * NetworkConfig.SimulatedJitterSeconds;
            delayed.Enqueue(deliver, due);
        }


        public void Connect()
        {
            client.MessageReceived += OnMessageReceived;
            client.Connected += (_, _) => Log.Info("Connected to server");
            client.Connect($"{NetworkConfig.ServerHost}:{NetworkConfig.Port}", useMessageHandlers: false);
        }

        /// <summary>Pump once per frame from Program.cs Update().</summary>
        public void Update()
        {
            client.Update();
            while (delayed.TryPeek(out _, out double due) && due <= Now)
                delayed.Dequeue().Invoke();
        }

        // The ONLY place the rest of the client can send from.
        public void SendInput(PlayerInputData input)
        {
            Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.PlayerInput);
            message.AddSerializable(input);
            client.Send(message);
        }

        public void SendFire(PlayerFireData fire)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ClientToServerId.PlayerFire);
            message.AddSerializable(fire);
            client.Send(message);
        }

        public void SendReload()
        {
            client.Send(Message.Create(MessageSendMode.Reliable, ClientToServerId.PlayerReload));
        }

        public void SendInteract()
        {
            client.Send(Message.Create(MessageSendMode.Reliable, ClientToServerId.PlayerInteract));
        }

        public void SendDig(PlayerDigData dig)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ClientToServerId.PlayerDig);
            message.AddSerializable(dig);
            client.Send(message);
        }

        public uint SendCommand(string command)
        {
            nextCommandRequestId++;
            if (nextCommandRequestId == 0) nextCommandRequestId++;

            Message message = Message.Create(MessageSendMode.Reliable, ClientToServerId.CommandRequest);
            message.AddSerializable(new CommandRequestData
            {
                RequestId = nextCommandRequestId,
                Command = command,
            });
            client.Send(message);
            return nextCommandRequestId;
        }


        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            // Decode NOW (Riptide reuses the Message after this returns), deliver
            // through Dispatch — immediately, or late when fake latency is on.
            switch ((ServerToClientId)e.MessageId)
            {
                case ServerToClientId.Welcome:
                    var welcome = e.Message.GetSerializable<WelcomeData>();
                    Dispatch(() =>
                    {
                        ClientId = welcome.ClientId;
                        Welcomed?.Invoke(welcome);
                    });
                    break;
                case ServerToClientId.PlayerSpawn:
                    var spawn = e.Message.GetSerializable<PlayerSpawnData>();
                    Dispatch(() => PlayerSpawned?.Invoke(spawn));
                    break;
                case ServerToClientId.PlayerDespawn:
                    var despawn = e.Message.GetSerializable<PlayerDespawnData>();
                    Dispatch(() => PlayerDespawned?.Invoke(despawn));
                    break;
                case ServerToClientId.PlayerPosition:
                    var position = e.Message.GetSerializable<PlayerPositionData>();
                    Dispatch(() => PlayerPositionReceived?.Invoke(position));
                    break;
                case ServerToClientId.ObjectSpawn:
                    var objSpawn = e.Message.GetSerializable<ObjectSpawnData>();
                    Dispatch(() => ObjectSpawned?.Invoke(objSpawn));
                    break;
                case ServerToClientId.ObjectDespawn:
                    var objDespawn = e.Message.GetSerializable<ObjectDespawnData>();
                    Dispatch(() => ObjectDespawned?.Invoke(objDespawn));
                    break;
                case ServerToClientId.ObjectState:
                    var objState = e.Message.GetSerializable<ObjectStateData>();
                    Dispatch(() => ObjectStateReceived?.Invoke(objState));
                    break;
                case ServerToClientId.PlayerFired:
                    var fired = e.Message.GetSerializable<PlayerFiredData>();
                    Dispatch(() => PlayerFired?.Invoke(fired));
                    break;
                case ServerToClientId.HitConfirm:
                    var confirm = e.Message.GetSerializable<HitConfirmData>();
                    Dispatch(() => HitConfirmed?.Invoke(confirm));
                    break;
                case ServerToClientId.TerrainEdit:
                    var edit = e.Message.GetSerializable<TerrainEditData>();
                    Dispatch(() => TerrainEdited?.Invoke(edit));
                    break;
                case ServerToClientId.CommandResult:
                    var commandResult = e.Message.GetSerializable<CommandResultData>();
                    Dispatch(() => CommandResultReceived?.Invoke(commandResult));
                    break;
            }
        }
    }
}
