using Riptide;

namespace Demiurge.GameServer
{
    internal class GameServer
    {
        private readonly Server server = new();
        private readonly GameWorld world;
        private readonly ServerCommandService commands;

        public GameServer(ServerOptions options)
        {
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
            server.Start(NetworkConfig.Port, maxClientCount: 100, useMessageHandlers: false);
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

        private void OnClientConnected(object? sender, ServerConnectedEventArgs e)
        {
            // Order matters. The stream is reserved first because its token rides in Welcome, and Welcome
            // is what tells the client to connect it; AddPlayer then queues the world into that stream.
            Guid chunkToken = world.RegisterChunkStream(e.Client.Id);

            Message msg = Message.Create(MessageSendMode.Reliable, ServerToClientId.Welcome);
            msg.AddSerializable(new WelcomeData { ClientId = e.Client.Id, ChunkToken = chunkToken });
            server.Send(msg, e.Client.Id);

            world.AddPlayer(e.Client.Id);
        }

        private void OnClientDisconnected(object? sender, ServerDisconnectedEventArgs e)
        {
            commands.Forget(e.Client.Id);
            world.RemovePlayer(e.Client.Id);
        }

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            switch ((ClientToServerId)e.MessageId)
            {
                case ClientToServerId.PlayerInput:
                    world.ApplyInput(e.FromConnection.Id, e.Message.GetSerializable<PlayerInputData>());
                    break;
                case ClientToServerId.PlayerFire:
                    world.ApplyFire(e.FromConnection.Id, e.Message.GetSerializable<PlayerFireData>());
                    break;
                case ClientToServerId.PlayerReload:
                    world.ApplyReload(e.FromConnection.Id);
                    break;
                case ClientToServerId.PlayerInteract:
                    world.ApplyInteract(e.FromConnection.Id);
                    break;
                case ClientToServerId.PlayerDig:
                    world.ApplyDig(e.FromConnection.Id, e.Message.GetSerializable<PlayerDigData>());
                    break;
                case ClientToServerId.CommandRequest:
                    var result = commands.Execute(
                        e.FromConnection.Id,
                        e.Message.GetSerializable<CommandRequestData>());
                    var response = Message.Create(MessageSendMode.Reliable, ServerToClientId.CommandResult);
                    response.AddSerializable(result);
                    server.Send(response, e.FromConnection.Id);
                    break;
            }
        }
    }
}
