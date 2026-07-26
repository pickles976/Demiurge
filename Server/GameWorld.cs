using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    internal class GameWorld
    {

        private readonly Dictionary<ushort, ServerPlayer> players = new();

        private readonly ObjectReplication objects;
        private readonly ItemSystem items;
        private readonly WeaponSystem weapons;
        private readonly ChunkStreamer chunks;

        private readonly Server server;

        private uint _Tick = 0;

        private const int MaxQueuedMoves = 3;

        /// <summary>
        /// The server's terrain, and the only authority on it. Clients receive it via
        /// <see cref="ChunkStreamer"/> and never generate any themselves.
        /// </summary>
        private readonly ChunkMap terrain = new();

        public GameWorld(Server server)
        {
            this.server = server;
            objects = new ObjectReplication(server);
            items = new ItemSystem(objects);
            weapons = new WeaponSystem(server, objects);

            // Before anything is placed: spawn positions are queried off the terrain.
            WorldGen.Generate(terrain);
            chunks = new ChunkStreamer(server, terrain);

            SpawnPickupOnSurface(ItemType.BodyArmor, 3f, 3f);
            SpawnPickupOnSurface(ItemType.AWP, 3f, 0f);
            SpawnPickupOnSurface(ItemType.Ak47, -3f, -3f);
            SpawnPickupOnSurface(ItemType.Glock, -5f, -5f);
        }

        /// <summary>Places a pickup on the ground at a world column, rather than at a guessed Y.</summary>
        private void SpawnPickupOnSurface(ItemType type, float worldX, float worldZ)
            => items.SpawnPickup(type, SurfaceQuery.SurfacePosition(terrain, worldX, worldZ));

        public void AddPlayer(ushort clientId)
        {
            foreach (var other in players.Values)              // catch the newcomer up
                server.Send(CreateSpawnMessage(other), clientId);

            objects.SendCatchUp(clientId); // catch the newcomer up on objects

            var player = new ServerPlayer { Id = clientId };
            player.Status = objects.Spawn(ObjectType.PlayerStatus, NetComponents.Owner | NetComponents.Health, player.Position,
            obj =>
            {
                obj.Owner = new OwnerState { PlayerId = clientId};
                obj.Health = new HealthState { Current = 100, Max = 100};
            });
            players[clientId] = player;
            server.SendToAll(CreateSpawnMessage(player));      // announce the newcomer

            chunks.QueueWorldFor(clientId);                    // terrain follows over the next few ticks
        }

        public void RemovePlayer(ushort clientId)
        {
                
                
            chunks.Forget(clientId);

            if (players.Remove(clientId, out var player))
            {
                items.DespawnFor(player);
                if (player.Status != null) objects.Despawn(player.Status.NetworkId);
            }


            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerDespawn);
            message.AddSerializable(
                new PlayerDespawnData
                {
                    PlayerId = clientId,
                    Tick = _Tick
                });
            server.SendToAll(message);
        }

        public void ApplyFire(ushort clientId, PlayerFireData fire)
        {
            if (players.TryGetValue(clientId, out var player))
                weapons.ApplyFire(player, fire, _Tick, players.Values);
        }

        public void ApplyReload(ushort clientId)
        {
            if (players.TryGetValue(clientId, out var player))
                weapons.ApplyReload(player, _Tick);
        }

        public void ApplyInteract(ushort clientId)
        {
            if (players.TryGetValue(clientId, out var player))
                items.ApplyInteract(player);
        }

        public void ApplyInput(ushort clientId, PlayerInputData input)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            if (input.Sequence <= player.LastReceivedSequence) return; // dupe or out of order

            if (!IsFinite(input.Intent) || !float.IsFinite(input.Yaw)) return;

            player.LastReceivedSequence = input.Sequence;
            player.PendingMoves.Enqueue(input);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public void Tick(float dt)
        {
            _Tick++;

            chunks.Tick();

            foreach (var player in players.Values)
            {

                // If the queue starts overflowing, consume at a faster rate
                int toProcess = player.PendingMoves.Count > MaxQueuedMoves ? 2 : 1;
                bool processedAny = false;

                for (int i = 0; i < toProcess && player.PendingMoves.TryDequeue(out var move); i++)
                {
                    player.Position = PlayerMovement.Step(player.Position, move.Intent, move.State, dt);
                    player.State = move.State;
                    player.Yaw = move.Yaw;
                    player.LastIntent = move.Intent;
                    player.LastProcessedSequence = move.Sequence;
                    processedAny = true;
                }

                // Queue starved, just reuse last player input
                if (!processedAny)
                    player.Position = PlayerMovement.Step(player.Position, player.LastIntent, player.State, dt);
            }

            // Save history
            foreach (var player in players.Values)
            {
                player.History.Store(_Tick, player.Position);
            }

            // Death and respawn
            foreach (var player in players.Values)
            {
                if (player.Status is not {} status || status.Health.Current > 0) continue;
                player.Position = Vector3.Zero;
                player.History.Clear();
                status.Health.Current = status.Health.Max;
                status.Dirty |= NetComponents.Health;
            }

            objects.BroadcastDirtyStatess(_Tick);
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
                    new PlayerPositionData
                    {
                        PlayerId = player.Id,
                        Tick = _Tick,
                        Position = player.Position,
                        Yaw = player.Yaw,
                        State = player.State,
                        LastProcessedSequence = player.LastProcessedSequence
                    });
                server.SendToAll(message);
            }
        }
    }
}
