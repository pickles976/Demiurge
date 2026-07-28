using Riptide;

namespace Demiurge.GameServer
{
    internal class GameServer
    {
        private readonly Server server = new();
        private readonly GameWorld world;
        private readonly ServerCommandService commands;

        public GameServer(bool allowCheats)
        {
            world = new GameWorld(server);
            commands = new ServerCommandService(world, allowCheats);
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
