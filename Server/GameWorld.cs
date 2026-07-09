using Riptide;

namespace Demiurge.GameServer
{
    internal class GameWorld
    {
        private readonly Dictionary<ushort, ServerPlayer> players = new();
        private readonly Server server;

        private uint _Tick = 0;

        private const int MaxQueuedMoves = 3;


        public GameWorld(Server server) => this.server = server;

        public void AddPlayer(ushort clientId)
        {
            foreach (var other in players.Values)              // catch the newcomer up
                server.Send(CreateSpawnMessage(other), clientId);

            var player = new ServerPlayer { Id = clientId };
            players[clientId] = player;
            server.SendToAll(CreateSpawnMessage(player));      // announce the newcomer
        }

        public void RemovePlayer(ushort clientId) {
            players.Remove(clientId);
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerDespawn);
            message.AddSerializable(
                new PlayerDespawnData { 
                    PlayerId = clientId, 
                    Tick=_Tick});
            server.SendToAll(message);
        }

        public void ApplyInput(ushort clientId, PlayerInputData input)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            if (input.Sequence <= player.LastReceivedSequence) return; // dupe or out of order

            player.LastReceivedSequence = input.Sequence;
            player.PendingMoves.Enqueue(input);
        }

        public void Tick(float dt)
        {
            _Tick++;

            foreach (var player in players.Values)
            {
                
                int toProcess = player.PendingMoves.Count > MaxQueuedMoves ? 2 : 1;
                bool processedAny = false;

                for (int i  = 0; i< toProcess && player.PendingMoves.TryDequeue(out var move); i++)
                {
                    player.Position = PlayerMovement.Step(player.Position, move.Intent, move.State, dt);
                    player.State = move.State;
                    player.Yaw = move.Yaw;
                    player.LastIntent = move.Intent;
                    player.LastProcessedSequence = move.Sequence;
                    processedAny = true;
                }

                if (!processedAny) // starved: assume input hasn't changed
                    player.Position = PlayerMovement.Step(player.Position, player.LastIntent, player.State, dt);
                
            }

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
                        Tick=_Tick,
                        Position = player.Position, 
                        Yaw = player.Yaw, 
                        State = player.State,
                        LastProcessedSequence = player.LastProcessedSequence });
                server.SendToAll(message); 
            }
        }
    }
}
