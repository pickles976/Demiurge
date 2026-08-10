using Demiurge.Net;

namespace Demiurge.GameServer
{
    internal class GameServer
    {
        private readonly INetServer server;
        private readonly GameWorld world;
        private readonly ServerCommandService commands;

        public GameServer(ServerOptions options)
        {
            server = options.Transport ?? new RiptideNetServer();

            if (options.RuntimeMap is not null && options.MapPath is not null)
                throw new ArgumentException("Specify either an in-memory runtime map or a map path, not both");
            if (options.InitialPlayerTeam is <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(options.InitialPlayerTeam),
                    "Initial player team must be positive");
            if (options.InitialNpcsPerTeam < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(options.InitialNpcsPerTeam),
                    "Initial NPC count cannot be negative");

            RuntimeMap? map = options.RuntimeMap
                ?? (options.MapPath is null ? null : RuntimeMapSerializer.Load(options.MapPath));
            world = new GameWorld(
                server,
                map,
                options.SpawnOverride,
                options.InitialPlayerTeam,
                options.InitialNpcsPerTeam);
            commands = new ServerCommandService(world, options.AllowCheats);
        }

        public void Start()
        {
            server.ClientConnected += OnClientConnected;
            server.ClientDisconnected += OnClientDisconnected;
            server.MessageReceived += OnMessageReceived;
            server.Start(NetworkConfig.Port, maxClientCount: 100);
        }

        public void PumpNetwork() => server.Update();

        public void Tick(float dt) => world.Tick(dt);

        public CommandResultData ExecuteConsoleCommand(string command)
            => commands.ExecuteConsole(command);

        public string Status()
            => $"map={world.MapName} actors={world.ActorSnapshot().Count}";

        public string Players()
        {
            var actors = world.ActorSnapshot();
            return actors.Count == 0
                ? "No actors"
                : string.Join(", ", actors.Select(actor => $"@{actor.Id} {(actor.IsMob ? "mob" : "player")}"));
        }

        public void Stop()
        {
            server.Stop();
            world.Stop();
        }

        private void OnClientConnected(object? sender, NetClientConnectedEventArgs e)
        {
            // Order matters. The stream is reserved first because its token rides in Welcome, and Welcome
            // is what tells the client to connect it; AddPlayer then queues the world into that stream.
            Guid chunkToken = world.RegisterChunkStream(e.ClientId);

            Message msg = Message.Create(MessageSendMode.Reliable, ServerToClientId.Welcome);
            msg.AddSerializable(new WelcomeData
            {
                ClientId = e.ClientId,
                ChunkToken = chunkToken,
                ProtocolVersion = NetworkConfig.ProtocolVersion,
                GameplayHash = ItemCatalog.Registry.GameplayHash,
            });
            server.Send(msg, e.ClientId);

            world.AddPlayer(e.ClientId);
        }

        private void OnClientDisconnected(object? sender, NetClientDisconnectedEventArgs e)
        {
            commands.Forget(e.ClientId);
            world.RemovePlayer(e.ClientId);
        }

        private void OnMessageReceived(object? sender, NetMessageReceivedEventArgs e)
        {
            switch ((ClientToServerId)e.MessageId)
            {
                case ClientToServerId.PlayerInput:
                    world.ApplyInput(e.ClientId, e.Message.GetSerializable<PlayerInputData>());
                    break;
                case ClientToServerId.PlayerFire:
                    world.ApplyFire(e.ClientId, e.Message.GetSerializable<PlayerFireData>());
                    break;
                case ClientToServerId.PlayerReload:
                    world.ApplyReload(e.ClientId);
                    break;
                case ClientToServerId.PlayerInteract:
                    world.ApplyInteract(e.ClientId);
                    break;
                case ClientToServerId.PlayerUse:
                    world.ApplyUse(e.ClientId);
                    break;
                case ClientToServerId.MortarFire:
                    world.ApplyMortarFire(
                        e.ClientId,
                        e.Message.GetSerializable<MortarFireData>());
                    break;
                case ClientToServerId.SelectClass:
                    world.ApplySelectClass(e.ClientId, e.Message.GetByte());
                    break;
                case ClientToServerId.PlayerDig:
                    world.ApplyDig(e.ClientId, e.Message.GetSerializable<PlayerDigData>());
                    break;
                case ClientToServerId.CommandRequest:
                    var result = commands.Execute(
                        e.ClientId,
                        e.Message.GetSerializable<CommandRequestData>());
                    var response = Message.Create(MessageSendMode.Reliable, ServerToClientId.CommandResult);
                    response.AddSerializable(result);
                    server.Send(response, e.ClientId);
                    break;
            }
        }
    }
}
