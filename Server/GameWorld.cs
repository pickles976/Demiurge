using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    internal class GameWorld
    {

        private readonly Dictionary<ushort, ServerPlayer> players = new();

        private readonly ObjectReplication objects;
        private readonly WeaponSystem weapons;

        private readonly Server server;

        private uint _Tick = 0;

        private const int MaxQueuedMoves = 3;

        public GameWorld(Server server)
        {
            this.server = server;
            objects = new ObjectReplication(server);
            weapons = new WeaponSystem(server, objects);

            // Transitional: inline armor init until ItemSystem.SpawnPickup exists (Task 5).
            var armor = ItemConfig.Get(ItemType.BodyArmor);
            objects.Spawn(ObjectType.ArmorPickup, NetComponents.Transform | NetComponents.Item | NetComponents.Armor, new Vector3(3f, 0f, 3f),
            obj =>
            {
                obj.Item = new ItemState { Type = ItemType.BodyArmor };
                obj.Armor = new ArmorState { MaxValue = armor.Armor!.Value, Current = armor.Armor.Value };
            });

            weapons.SpawnPickup(ItemType.AWP, new Vector3(3f, 0f, 0f));
            weapons.SpawnPickup(ItemType.Ak47, new Vector3(-3f, 0f, -3f));
            weapons.SpawnPickup(ItemType.Glock, new Vector3(-5f, 0f, -5f));
        }

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
        }

        public void RemovePlayer(ushort clientId)
        {
                
                
            if (players.Remove(clientId, out var player))
            {
                weapons.DespawnFor(player);
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

                weapons.TryPickup(player);
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
