using Riptide;
using System.Diagnostics;
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
        private readonly ActivityFeedSystem activityFeed;
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
        private readonly int? initialPlayerTeam;
        private bool initialPlayerTeamAssigned;
        private readonly RuntimePlacement? initialPlayerSpawn;
        private bool initialPlayerSpawnUsed;
        private readonly Dictionary<int, int> nextPlayerSpawnByTeam = [];
        public string MapName { get; }

        /// <summary>
        /// The server's terrain, and the only authority on it. Clients receive it via
        /// <see cref="ChunkTcpServer"/> and never generate any themselves.
        /// </summary>
        private readonly ChunkMap terrain;

        public GameWorld(
            Server server,
            RuntimeMap? runtimeMap = null,
            Vector3? spawnOverride = null,
            int? initialPlayerTeam = null,
            int initialNpcsPerTeam = 0)
        {
            this.server = server;
            this.spawnOverride = spawnOverride;
            this.initialPlayerTeam = initialPlayerTeam;
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
            var initialSpawns = InitialTeamSpawnPlan.Create(
                runtimeMap?.Placements ?? [],
                initialPlayerTeam,
                initialNpcsPerTeam);
            initialPlayerSpawn = initialSpawns.PlayerSpawn;

            objects = new ObjectReplication(server);
            items = new ItemSystem(objects);
            activityFeed = new ActivityFeedSystem(server);
            weapons = new WeaponSystem(server, objects, terrain, activityFeed);
            flags = new FlagSystem(objects, activityFeed);
            terrainEdits = new TerrainSystem(server, terrain);
            grenades = new GrenadeSystem(
                objects,
                items,
                terrainEdits,
                terrain,
                activityFeed);
            mobs = new MobSystem(terrain, terrainEdits, weapons, flags, grenades);

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

                SpawnMob();
                SpawnMob();
            }
            else
            {
                SpawnRuntimePlacements(runtimeMap.Placements);
                SpawnInitialTeamMobs(initialSpawns.NpcSpawns);
            }
        }

        private void SpawnInitialTeamMobs(IReadOnlyList<RuntimePlacement> spawns)
        {
            foreach (var spawn in spawns)
            {
                Vector3 position = NavTraversal.TryFindNearestStandable(
                        terrain,
                        spawn.Position,
                        horizontalRadius: 8,
                        out var spawnCell)
                    ? NavTraversal.Position(terrain, spawnCell)
                    : spawn.Position;
                var mob = SpawnMob(position, spawn.Team);
                mob.Yaw = spawn.Yaw;
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
            var mob = mobs.CreateMob(AllocateMobId(), position, team);
            mob.Status = objects.Spawn(ObjectType.PlayerStatus, NetComponents.Owner | NetComponents.Health, mob.Position,
            obj =>
            {
                obj.Owner = new OwnerState { PlayerId = mob.Id };
                obj.Health = new HealthState { Current = 100, Max = 100 };
            });
            players[mob.Id] = mob;
            items.SpawnInfantryLoadout(mob);
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
            => WeaponConfig.Get(type) is not null
                ? items.SpawnHotbar(
                    actor,
                    type,
                    HotbarConfig.SlotFor(type),
                    dropReplaced: false)
                : items.SpawnEquipped(actor, type, dropReplaced: false);

        public bool IsSpawnableColumn(float worldX, float worldZ)
        {
            var chunk = ChunkTransforms.ChunkAt((int)MathF.Floor(worldX), (int)MathF.Floor(worldZ));
            return chunk.x >= WorldGen.MeshableMin.x && chunk.x <= WorldGen.MeshableMax.x
                && chunk.z >= WorldGen.MeshableMin.z && chunk.z <= WorldGen.MeshableMax.z;
        }

        public Vector3 SurfacePosition(float worldX, float worldZ)
            => SurfaceQuery.SurfacePosition(terrain, worldX, worldZ);

        public string AiStats() => mobs.Stats();

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
        public void Stop()
        {
            mobs.Dispose();
            chunks.Dispose();
        }

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
            items.SpawnInfantryLoadout(player);

            chunks.QueueWorldFor(clientId);                    // terrain follows over the next few ticks
        }

        public void RemovePlayer(ushort clientId)
        {
            chunks.Forget(clientId);
            RemoveActor(clientId);
        }

        private void RemoveActor(ushort actorId)
        {
            if (players.Remove(actorId, out var player))
            {
                if (player.IsMob)
                    mobs.RemoveMob(player);
                items.DespawnFor(player);
                if (player.Status != null) objects.Despawn(player.Status.NetworkId);
            }

            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerDespawn);
            message.AddSerializable(
                new PlayerDespawnData
                {
                    PlayerId = actorId,
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
            if (players.TryGetValue(clientId, out var player)
                && player.Status is { Health.Current: > 0 })
                weapons.ApplyReload(player, _Tick);
        }

        public void ApplyDig(ushort clientId, PlayerDigData dig)
        {
            if (players.TryGetValue(clientId, out var player)
                && player.Status is { Health.Current: > 0 }
                && HotbarConfig.IsValid(dig.Hotbar))
            {
                player.Hotbar = dig.Hotbar;
                terrainEdits.ApplyDig(player, dig, _Tick);
            }
        }

        public void ApplyInteract(ushort clientId)
        {
            if (players.TryGetValue(clientId, out var player)
                && player.Status is { Health.Current: > 0 })
                items.ApplyInteract(player);
        }

        public void ApplyInput(ushort clientId, PlayerInputData input)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            if (input.Sequence <= player.LastReceivedSequence) return; // dupe or out of order

            if (!IsFinite(input.Intent) || !float.IsFinite(input.Yaw) || !float.IsFinite(input.Pitch)) return;
            if (!HotbarConfig.IsValid(input.Hotbar)) return;

            player.LastReceivedSequence = input.Sequence;
            if (player.Status is not { Health.Current: > 0 })
            {
                // A packet already in flight when death was replicated must never become movement
                // waiting to fire on the respawn wave. Acknowledge it without simulating it.
                player.LastProcessedSequence = input.Sequence;
                return;
            }
            player.Hotbar = input.Hotbar;
            player.PendingMoves.Enqueue(input);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public void Tick(float dt)
        {
            _Tick++;
            mobs.BeginTick(_Tick, players.Values);
            long mobMovementTicks = 0;
            int mobCount = 0;

            foreach (var player in players.Values)
            {
                // Dead actors remain at the death position until the next shared respawn wave.
                // Their zero-health status and stationary position continue replicating so clients
                // can keep the body camera and cosmetic ragdoll anchored to the right location.
                if (player.Status is { Health.Current: 0 })
                    continue;

                if (player.IsMob)
                {
                    long started = Stopwatch.GetTimestamp();
                    mobs.Step(player, dt, _Tick, players.Values);
                    mobMovementTicks += Stopwatch.GetTimestamp() - started;
                    mobCount++;
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
            mobs.RecordTick(mobMovementTicks, mobCount);
            while (mobs.TryDequeueStuckMob(out ushort stuckMobId))
            {
                if (!players.TryGetValue(stuckMobId, out var stuck)
                    || !stuck.IsMob)
                    continue;
                activityFeed.ReportNpcDeleted(
                    stuckMobId,
                    "stuck for 60 seconds while navigating");
                RemoveActor(stuckMobId);
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
                    player.RespawnTick = RespawnConfig.NextWaveTick(_Tick);
                    player.PendingMoves.Clear();
                    player.LastIntent = Vector3.Zero;
                    player.State = 0;
                    continue;
                }
                if (_Tick < player.RespawnTick) continue;

                player.Move = SpawnPlayerMove(player.Team, useOverride: false);
                player.History.Clear();
                player.PendingMoves.Clear();
                player.LastIntent = Vector3.Zero;
                player.State = 0;
                player.Hotbar = HotbarSlot.Primary;
                player.NextFireTick = 0;
                player.ReloadDoneTick = 0;
                player.NextGrenadeThrowTick = 0;
                player.Spread = default;
                status.Health.Current = status.Health.Max;
                status.Dirty |= NetComponents.Health;
                items.RefillRespawnLoadout(player);
                player.RespawnTick = 0;
            }

            objects.BroadcastDirtyStatess(_Tick);
            BroadcastPositions();
        }

        private int AssignPlayerTeam()
        {
            if (!initialPlayerTeamAssigned && initialPlayerTeam is { } team)
            {
                initialPlayerTeamAssigned = true;
                return team;
            }

            return playableTeams
                .OrderBy(team => players.Values.Count(player => !player.IsMob && player.Team == team))
                .ThenBy(team => team)
                .First();
        }

        private MoveState SpawnPlayerMove(int team, bool useOverride)
        {
            // The editor playtest hands us the fly camera's exact position and it outranks the
            // map, so you drop in where you were looking. Not grounded: the point is wherever the
            // camera was, in the air as often as not, and gravity takes it from there.
            if (useOverride && spawnOverride is { } forced)
            {
                initialPlayerSpawnUsed = true;
                return new MoveState { Position = forced, Velocity = Vector3.Zero, Grounded = false };
            }

            if (!initialPlayerSpawnUsed
                && initialPlayerSpawn is { } reserved
                && reserved.Team == team)
            {
                initialPlayerSpawnUsed = true;
                return MoveAtSpawn(reserved);
            }

            if (flags.TrySpawnPosition(team, out var flag))
                return PlayerMovement.SpawnAt(terrain, flag.X, flag.Z);

            if (playerSpawns.Length == 0)
                return PlayerMovement.SpawnAt(terrain, SpawnX, SpawnZ);

            var teamSpawns = playerSpawns.Where(spawn => spawn.Team == team).ToArray();
            // Never silently place an actor at an enemy base. A malformed map can fall back to the
            // neutral generated column, but a valid team always uses its own placements.
            if (teamSpawns.Length == 0)
                return PlayerMovement.SpawnAt(terrain, SpawnX, SpawnZ);
            int next = nextPlayerSpawnByTeam.GetValueOrDefault(team);
            nextPlayerSpawnByTeam[team] = (next + 1) % teamSpawns.Length;
            var spawn = teamSpawns[next % teamSpawns.Length];
            return MoveAtSpawn(spawn);
        }

        private MoveState MoveAtSpawn(RuntimePlacement spawn)
        {
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
                        RespawnTick = player.RespawnTick,
                    });
                server.SendToAll(message);
            }
        }
    }
}
