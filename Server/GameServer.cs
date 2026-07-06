using Riptide;

namespace Demiurge.GameServer
{
    internal class GameServer
    {
        private readonly Server server = new();   // Riptide's Server, now private

        public void Start()
        {
            server.ClientConnected += OnClientConnected;
            server.ClientDisconnected += OnClientDisconnected;
            server.MessageReceived += OnMessageReceived;
            server.Start(NetworkConfig.Port, maxClientCount: 100, useMessageHandlers: false);
        }

        public void Update()
        {
            server.Update();
            PlayerHandle.SendPositions(server);   // transitional — see note below
        }

        public void Stop() => server.Stop();

        private void OnClientConnected(object? sender, ServerConnectedEventArgs e)
        {
            Message msg = Message.Create(MessageSendMode.Reliable, (ushort)ServerToClientId.Welcome);
            msg.AddSerializable(new WelcomeData { ClientId = e.Client.Id });
            server.Send(msg, e.Client.Id);

            new PlayerHandle(e.Client.Id, server);   // transitional
        }

        private void OnClientDisconnected(object? sender, ServerDisconnectedEventArgs e)
        {
            PlayerHandle.List.Remove(e.Client.Id);   // you currently never remove players
        }

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            switch ((ClientToServerId)e.MessageId)
            {
                case ClientToServerId.PlayerPosition:
                    if (PlayerHandle.List.TryGetValue(e.FromConnection.Id, out var player))
                        player.ApplyPosition(e.Message.GetSerializable<ClientPositionData>());
                    break;
            }
        }
    }
}
