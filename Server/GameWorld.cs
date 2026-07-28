using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    internal class GameWorld : ICommandWorld
    {

        private readonly Dictionary<ushort, ServerPlayer> players = new();

        private readonly ObjectReplication objects;
        private readonly ItemSystem items;
        private readonly MobSystem mobs;
        private readonly WeaponSystem weapons;
        private readonly TerrainSystem terrainEdits;
        private readonly ChunkTcpServer chunks;

        private readonly Server server;

        private uint _Tick = 0;
        private ushort nextMobId = 60000;

        private const int MaxQueuedMoves = 3;

        /// <summary>Spawn column. Y comes off the terrain, never guessed.</summary>
        private const float SpawnX = 0f;
        private const float SpawnZ = 0f;

        /// <summary>
        /// The server's terrain, and the only authority on it. Clients receive it via
        /// <see cref="ChunkTcpServer"/> and never generate any themselves.
        /// </summary>
        private readonly ChunkMap terrain = new();

        public GameWorld(Server server)
        {
            this.server = server;
            objects = new ObjectReplication(server);
            items = new ItemSystem(objects);
            mobs = new MobSystem(terrain);
            weapons = new WeaponSystem(server, objects, terrain);
            terrainEdits = new TerrainSystem(server, terrain);

            // Before anything is placed: spawn positions are queried off the terrain.
            WorldGen.Generate(terrain);
            chunks = new ChunkTcpServer(terrain);
            chunks.Start();

            // Trees are temporarily disabled. TreeSystem and its client views remain available.
            // new TreeSystem(objects, terrain).SpawnInitialTrees();

            SpawnPickupOnSurface(ItemType.BodyArmor, 3f, 3f);
            SpawnPickupOnSurface(ItemType.AWP, 3f, 0f);
            SpawnPickupOnSurface(ItemType.Ak47, -3f, -3f);
            SpawnPickupOnSurface(ItemType.Glock, -5f, -5f);

            items.SpawnEquipped(SpawnMob(), ItemType.Ak47);
            items.SpawnEquipped(SpawnMob(), ItemType.Ak47);
        }

        /// <summary>Places a pickup on the ground at a world column, rather than at a guessed Y.</summary>
        private void SpawnPickupOnSurface(ItemType type, float worldX, float worldZ)
            => items.SpawnPickup(type, SurfaceQuery.SurfacePosition(terrain, worldX, worldZ));

        public ServerPlayer SpawnMob(Vector3? requestedPosition = null)
        {
            var position = requestedPosition ?? mobs.RandomSpawnPoint();
            var mob = mobs.CreateMob(AllocateMobId(), position);
            mob.Status = objects.Spawn(ObjectType.PlayerStatus, NetComponents.Owner | NetComponents.Health, mob.Position,
            obj =>
            {
                obj.Owner = new OwnerState { PlayerId = mob.Id };
                obj.Health = new HealthState { Current = 100, Max = 100 };
            });
            players[mob.Id] = mob;
            server.SendToAll(CreateSpawnMessage(mob));
            return mob;
        }

        public ServerObject SpawnPickup(ItemType type, Vector3 position)
            => items.SpawnPickup(type, position);

        public bool TryGetActor(ushort actorId, out ServerPlayer actor)
            => players.TryGetValue(actorId, out actor!);

        public ServerObject Equip(ServerPlayer actor, ItemType type)
            => items.SpawnEquipped(actor, type, dropReplaced: false);

        public bool IsSpawnableColumn(float worldX, float worldZ)
        {
            var chunk = ChunkTransforms.ChunkAt((int)MathF.Floor(worldX), (int)MathF.Floor(worldZ));
            return chunk.x >= WorldGen.MeshableMin.x && chunk.x <= WorldGen.MeshableMax.x
                && chunk.z >= WorldGen.MeshableMin.z && chunk.z <= WorldGen.MeshableMax.z;
        }

        public Vector3 SurfacePosition(float worldX, float worldZ)
            => SurfaceQuery.SurfacePosition(terrain, worldX, worldZ);

        private ushort AllocateMobId()
        {
            const ushort firstMobId = 60000;
            for (int attempts = 0; attempts <= ushort.MaxValue - firstMobId; attempts++)
            {
                ushort candidate = nextMobId;
                nextMobId = candidate == ushort.MaxValue ? firstMobId : (ushort)(candidate + 1);
                if (!players.ContainsKey(candidate)) return candidate;
            }

            throw new InvalidOperationException("No mob actor ids are available");
        }

        /// <summary>
        /// Reserves this client's terrain stream and returns the token it must present on it. Must happen
        /// before the client is welcomed, since the token rides in the Welcome message, and before
        /// <see cref="AddPlayer"/>, which queues the world into the stream this creates.
        /// </summary>
        public Guid RegisterChunkStream(ushort clientId) => chunks.Register(clientId);

        /// <summary>Stops the terrain listener and its writer threads.</summary>
        public void Stop() => chunks.Dispose();

        public void AddPlayer(ushort clientId)
        {
            foreach (var other in players.Values)              // catch the newcomer up
                server.Send(CreateSpawnMessage(other), clientId);

            objects.SendCatchUp(clientId); // catch the newcomer up on objects

            var player = new ServerPlayer { Id = clientId };
            player.Move = PlayerMovement.SpawnAt(terrain, SpawnX, SpawnZ);
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

        public void ApplyDig(ushort clientId, PlayerDigData dig)
        {
            if (players.TryGetValue(clientId, out var player))
                terrainEdits.ApplyDig(player, dig, _Tick);
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

            if (!IsFinite(input.Intent) || !float.IsFinite(input.Yaw) || !float.IsFinite(input.Pitch)) return;

            player.LastReceivedSequence = input.Sequence;
            player.PendingMoves.Enqueue(input);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public void Tick(float dt)
        {
            _Tick++;

            foreach (var player in players.Values)
            {
                if (player.IsMob)
                {
                    mobs.Step(player, dt);
                    continue;
                }

                // If the queue starts overflowing, consume at a faster rate
                int toProcess = player.PendingMoves.Count > MaxQueuedMoves ? 2 : 1;
                bool processedAny = false;

                for (int i = 0; i < toProcess && player.PendingMoves.TryDequeue(out var move); i++)
                {
                    PlayerMovement.Step(terrain, ref player.Move, move.Intent, move.State, dt);
                    player.State = move.State;
                    player.Yaw = move.Yaw;
                    player.Pitch = move.Pitch;
                    player.LastIntent = move.Intent;
                    player.LastProcessedSequence = move.Sequence;
                    processedAny = true;
                }

                // Queue starved, just reuse last player input
                if (!processedAny)
                    PlayerMovement.Step(terrain, ref player.Move, player.LastIntent, player.State, dt);
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
                player.Move = PlayerMovement.SpawnAt(terrain, SpawnX, SpawnZ);
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
                        Pitch = player.Pitch,
                        State = player.State,
                        LastProcessedSequence = player.LastProcessedSequence,
                        Velocity = player.Move.Velocity,
                        Grounded = player.Move.Grounded
                    });
                server.SendToAll(message);
            }
        }
    }
}
