using Riptide;

namespace Demiurge.GameServer
{
    internal class GameWorld
    {
        private readonly Dictionary<ushort, ServerPlayer> players = new();
        private readonly Server server;

        public GameWorld(Server server) => this.server = server;

        public void AddPlayer(ushort clientId)
        {
            foreach (var other in players.Values)              // catch the newcomer up
                server.Send(CreateSpawnMessage(other), clientId);

            var player = new ServerPlayer { Id = clientId };
            players[clientId] = player;
            server.SendToAll(CreateSpawnMessage(player));      // announce the newcomer
        }

        public void RemovePlayer(ushort clientId) => players.Remove(clientId);

        public void ApplyInput(ushort clientId, PlayerInputData input)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            player.PendingIntent = input.Intent;
            player.State = input.State;
            player.Yaw = input.Yaw;
        }

        public void Tick(float dt)
        {
            foreach (var player in players.Values)
                player.Position = PlayerMovement.Step(player.Position, player.PendingIntent, player.State, dt);

            BroadcastPositions();
        }

        private Message CreateSpawnMessage(ServerPlayer player)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerSpawn);
            message.AddSerializable(new PlayerSpawnData { PlayerId = player.Id, Position = player.Position });
            return message;
        }

        private void BroadcastPositions()
        {
            foreach (var player in players.Values)
            {
                Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.PlayerPosition);
                message.AddSerializable(
                    new PlayerPositionData { 
                        PlayerId = player.Id, 
                        Position = player.Position, 
                        Yaw = player.Yaw, 
                        State = player.State });
                server.SendToAll(message, player.Id);   // second arg: don't echo to the owner
            }
        }
    }
}
