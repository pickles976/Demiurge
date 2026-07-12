using Riptide;

namespace Demiurge.GameServer
{
    internal class GameServer
    {
        private readonly Server server = new();
        private readonly GameWorld world;

        public GameServer() => world = new GameWorld(server);

        public void Start()
        {
            server.ClientConnected += OnClientConnected;
            server.ClientDisconnected += OnClientDisconnected;
            server.MessageReceived += OnMessageReceived;
            server.Start(NetworkConfig.Port, maxClientCount: 100, useMessageHandlers: false);
        }

        public void PumpNetwork() => server.Update();

        public void Tick(float dt) => world.Tick(dt);

        public void Stop() => server.Stop();

        private void OnClientConnected(object? sender, ServerConnectedEventArgs e)
        {
            Message msg = Message.Create(MessageSendMode.Reliable, ServerToClientId.Welcome);
            msg.AddSerializable(new WelcomeData { ClientId = e.Client.Id });
            server.Send(msg, e.Client.Id);

            world.AddPlayer(e.Client.Id);
        }

        private void OnClientDisconnected(object? sender, ServerDisconnectedEventArgs e)
        {
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
            }
        }
    }
}
