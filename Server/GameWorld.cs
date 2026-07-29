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
        private readonly GrenadeSystem grenades;
        private readonly FlagSystem flags;
        private readonly TerrainSystem terrainEdits;
        private readonly ChunkTcpServer chunks;

        private readonly Server server;

        private uint _Tick = 0;
        private ushort nextMobId = 60000;

        private const int MaxQueuedMoves = 3;

        /// <summary>Spawn column. Y comes off the terrain, never guessed.</summary>
        private const float SpawnX = 0f;
        private const float SpawnZ = 0f;
        private readonly RuntimePlacement[] playerSpawns;
        private readonly int[] playableTeams;
        private readonly Vector3? spawnOverride;
        private readonly Dictionary<int, int> nextPlayerSpawnByTeam = [];
        public string MapName { get; }

        /// <summary>
        /// The server's terrain, and the only authority on it. Clients receive it via
        /// <see cref="ChunkTcpServer"/> and never generate any themselves.
        /// </summary>
        private readonly ChunkMap terrain;

        public GameWorld(Server server, RuntimeMap? runtimeMap = null, Vector3? spawnOverride = null)
        {
            this.server = server;
            this.spawnOverride = spawnOverride;
            terrain = runtimeMap?.Terrain ?? new ChunkMap();
            MapName = runtimeMap?.Name ?? "generated";

            if (runtimeMap is null)
                WorldGen.Generate(terrain);

            playerSpawns = runtimeMap?.Placements
                .Where(placement => placement.Kind == RuntimePlacementKind.PlayerSpawn)
                .ToArray()
                ?? [];
            playableTeams = playerSpawns
                .Select(placement => placement.Team)
                .Where(team => team > 0)
                .Distinct()
                .Order()
                .DefaultIfEmpty(1)
                .ToArray();

            objects = new ObjectReplication(server);
            items = new ItemSystem(objects);
            mobs = new MobSystem(terrain);
            weapons = new WeaponSystem(server, objects, terrain);
            terrainEdits = new TerrainSystem(server, terrain);
            grenades = new GrenadeSystem(objects, items, terrainEdits, terrain);
            flags = new FlagSystem(objects);

            chunks = new ChunkTcpServer(terrain);
            chunks.Start();

            // Trees are temporarily disabled. TreeSystem and its client views remain available.
            // new TreeSystem(objects, terrain).SpawnInitialTrees();

            if (runtimeMap is null)
            {
                SpawnPickupOnSurface(ItemType.BodyArmor, 3f, 3f);
                SpawnPickupOnSurface(ItemType.AWP, 3f, 0f);
                SpawnPickupOnSurface(ItemType.Ak47, -3f, -3f);
                SpawnPickupOnSurface(ItemType.Glock, -5f, -5f);
                SpawnPickupOnSurface(ItemType.Grenade, 1.5f, 1.5f);

                items.SpawnEquipped(SpawnMob(), ItemType.Ak47);
                items.SpawnEquipped(SpawnMob(), ItemType.Ak47);
            }
            else
            {
                SpawnRuntimePlacements(runtimeMap.Placements);
            }
        }

        private void SpawnRuntimePlacements(IReadOnlyList<RuntimePlacement> placements)
        {
            foreach (var placement in placements)
            {
                switch (placement.Kind)
                {
                    case RuntimePlacementKind.Pickup:
                        items.SpawnPickup(placement.Item, placement.Position);
                        break;
                    case RuntimePlacementKind.Mob:
                        var mob = SpawnMob(placement.Position, placement.Team);
                        mob.Yaw = placement.Yaw;
                        items.SpawnEquipped(
                            mob,
                            placement.Item == default ? ItemType.Ak47 : placement.Item);
                        break;
                    case RuntimePlacementKind.Flag:
                        flags.Spawn(placement.Position);
                        break;
                }
            }
        }

        /// <summary>Places a pickup on the ground at a world column, rather than at a guessed Y.</summary>
        private void SpawnPickupOnSurface(ItemType type, float worldX, float worldZ)
            => items.SpawnPickup(type, SurfaceQuery.SurfacePosition(terrain, worldX, worldZ));

        public ServerPlayer SpawnMob(Vector3? requestedPosition = null)
            => SpawnMob(requestedPosition, team: 1);

        private ServerPlayer SpawnMob(Vector3? requestedPosition, int team)
        {
            var position = requestedPosition ?? mobs.RandomSpawnPoint();
            var mob = mobs.CreateMob(AllocateMobId(), position);
            mob.Team = team > 0 ? team : 1;
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

        public IReadOnlyList<(ushort Id, bool IsMob)> ActorSnapshot()
            => players.Values
                .Select(player => (player.Id, player.IsMob))
                .OrderBy(actor => actor.Id)
                .ToArray();

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

            int team = AssignPlayerTeam();
            var player = new ServerPlayer { Id = clientId, Team = team };
            player.Move = SpawnPlayerMove(team, useOverride: true);
            player.Status = objects.Spawn(ObjectType.PlayerStatus, NetComponents.Owner | NetComponents.Health, player.Position,
            obj =>
            {
                obj.Owner = new OwnerState { PlayerId = clientId};
                obj.Health = new HealthState { Current = 100, Max = 100};
            });
            players[clientId] = player;
            server.SendToAll(CreateSpawnMessage(player));      // announce the newcomer
            items.SpawnHotbar(
                player,
                ItemType.Grenade,
                HotbarSlot.Grenade,
                ammo: WeaponConfig.Require(ItemType.Grenade).MagazineCapacity);

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
            if (players.TryGetValue(clientId, out var player)
                && player.Status is { Health.Current: > 0 })
            {
                if (!HotbarConfig.IsValid(fire.Hotbar)) return;
                player.Hotbar = fire.Hotbar;
                if (grenades.IsGrenadeEquipped(player))
                    grenades.ApplyThrow(player, fire, _Tick);
                else
                    weapons.ApplyFire(player, fire, _Tick);
            }
        }

        public void ApplyReload(ushort clientId)
        {
            if (players.TryGetValue(clientId, out var player))
                weapons.ApplyReload(player, _Tick);
        }

        public void ApplyDig(ushort clientId, PlayerDigData dig)
        {
            if (players.TryGetValue(clientId, out var player)
                && HotbarConfig.IsValid(dig.Hotbar))
            {
                player.Hotbar = dig.Hotbar;
                terrainEdits.ApplyDig(player, dig, _Tick);
            }
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
            if (!HotbarConfig.IsValid(input.Hotbar)) return;

            player.LastReceivedSequence = input.Sequence;
            player.Hotbar = input.Hotbar;
            player.PendingMoves.Enqueue(input);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public void Tick(float dt)
        {
            _Tick++;

            foreach (var player in players.Values)
            {
                // Leave a dead actor in place for one replicated tick. Besides preventing input
                // during the tiny death window, this makes zero health an observable event instead
                // of being overwritten by the respawn later in the same server tick.
                if (player.Status is { Health.Current: 0 })
                    continue;

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

            flags.Tick(dt, players.Values);

            // Save history
            foreach (var player in players.Values)
            {
                player.History.Store(_Tick, player.Position);
            }

            weapons.Tick(dt, players.Values);
            grenades.Tick(dt, _Tick, players.Values);

            // Death and respawn
            foreach (var player in players.Values)
            {
                if (player.Status is not {} status || status.Health.Current > 0) continue;

                if (player.RespawnTick == 0)
                {
                    player.RespawnTick = _Tick + 1;
                    continue;
                }
                if (_Tick < player.RespawnTick) continue;

                player.Move = SpawnPlayerMove(player.Team, useOverride: false);
                player.History.Clear();
                status.Health.Current = status.Health.Max;
                status.Dirty |= NetComponents.Health;
                items.SpawnHotbar(
                    player,
                    ItemType.Grenade,
                    HotbarSlot.Grenade,
                    ammo: WeaponConfig.Require(ItemType.Grenade).MagazineCapacity);
                player.RespawnTick = 0;
            }

            objects.BroadcastDirtyStatess(_Tick);
            BroadcastPositions();
        }

        private int AssignPlayerTeam()
            => playableTeams
                .OrderBy(team => players.Values.Count(player => !player.IsMob && player.Team == team))
                .ThenBy(team => team)
                .First();

        private MoveState SpawnPlayerMove(int team, bool useOverride)
        {
            // The editor playtest hands us the fly camera's exact position and it outranks the
            // map, so you drop in where you were looking. Not grounded: the point is wherever the
            // camera was, in the air as often as not, and gravity takes it from there.
            if (useOverride && spawnOverride is { } forced)
                return new MoveState { Position = forced, Velocity = Vector3.Zero, Grounded = false };

            if (flags.TrySpawnPosition(team, out var flag))
                return PlayerMovement.SpawnAt(terrain, flag.X, flag.Z);

            if (playerSpawns.Length == 0)
                return PlayerMovement.SpawnAt(terrain, SpawnX, SpawnZ);

            var teamSpawns = playerSpawns.Where(spawn => spawn.Team == team).ToArray();
            if (teamSpawns.Length == 0) teamSpawns = playerSpawns;
            int next = nextPlayerSpawnByTeam.GetValueOrDefault(team);
            nextPlayerSpawnByTeam[team] = (next + 1) % teamSpawns.Length;
            var spawn = teamSpawns[next % teamSpawns.Length];
            var move = PlayerMovement.SpawnAt(terrain, spawn.Position.X, spawn.Position.Z);
            move.Position = spawn.Position;
            return move;
        }

        private Message CreateSpawnMessage(ServerPlayer player)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerSpawn);
            message.AddSerializable(new PlayerSpawnData
            {
                PlayerId = player.Id,
                Position = player.Position,
                Team = player.Team,
            });
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
                        Grounded = player.Move.Grounded,
                        Hotbar = player.Hotbar,
                    });
                server.SendToAll(message);
            }
        }
    }
}
