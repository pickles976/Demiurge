using Demiurge.Net;
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
        private readonly TicketSystem tickets;
        private readonly TerrainSystem terrainEdits;
        private readonly ActivityFeedSystem activityFeed;
        private readonly ChunkTcpServer chunks;

        private readonly INetServer server;

        private uint _Tick = 0;
        private readonly Dictionary<int, int> npcSpawnOrdinalsByTeam = [];
        private ServerTickBreakdown serverBreakdown;

        /// <summary>
        /// Where a server tick goes, once a second. In singleplayer this whole thing runs inside the
        /// client's frame, so every millisecond here is a millisecond off the frame budget.
        /// </summary>
        private struct ServerTickBreakdown
        {
            private long windowStart;
            private int ticks;
            private long aiTicks, actorTicks, flagTicks, weaponTicks, grenadeTicks, broadcastTicks;
            private long projectileSum;
            private double worstMs;

            /// <summary>
            /// Rolling tick durations, for the "30 TPS 99% of the time" target.
            /// </summary>
            /// <remarks>
            /// Reported on a slower cadence than the breakdown above, because a one-second window holds
            /// about 30 samples and a 99th percentile drawn from 30 samples is just the maximum.
            /// <para>
            /// This measures tick DURATION against the 33.3 ms budget, which is a necessary condition
            /// for sustaining 30 TPS rather than a direct measurement of it: the loop cannot run 30
            /// ticks in a second if a tick costs more than a thirtieth of one. The achieved rate is the
            /// `N ticks` count on the line above, and the two should be read together — duration inside
            /// budget with a rate below 30 would mean something is stalling the loop rather than the
            /// tick being too expensive.
            /// </para>
            /// </remarks>
            private PercentileWindow? tickDurations;
            private long percentileWindowStart;

            public void Record(
                long start, long afterAi, long afterActors, long afterFlags,
                long afterWeapons, long afterGrenades, long end, int projectiles)
            {
                ticks++;
                aiTicks += afterAi - start;
                actorTicks += afterActors - afterAi;
                flagTicks += afterFlags - afterActors;
                weaponTicks += afterWeapons - afterFlags;
                grenadeTicks += afterGrenades - afterWeapons;
                broadcastTicks += end - afterGrenades;
                projectileSum += projectiles;

                double tickMs = (end - start) * 1000.0 / Stopwatch.Frequency;
                worstMs = Math.Max(worstMs, tickMs);

                tickDurations ??= new PercentileWindow(PerformanceTargets.ServerTickBudgetMs);
                tickDurations.Add((float)tickMs);

                long now = Stopwatch.GetTimestamp();

                if (percentileWindowStart == 0) percentileWindowStart = now;
                if ((now - percentileWindowStart) / (double)Stopwatch.Frequency >= 5.0)
                {
                    Console.WriteLine($"[ServerTick]: {tickDurations.Summarize().Format("tick")}");
                    percentileWindowStart = now;
                }

                if (windowStart == 0) windowStart = now;
                if ((now - windowStart) / (double)Stopwatch.Frequency < 1.0) return;

                double perTick = 1000.0 / Stopwatch.Frequency / ticks;
                Console.WriteLine(
                    $"[ServerTick]: server tick: {ticks} ticks | ai {aiTicks * perTick:F2} ms "
                  + $"| actors {actorTicks * perTick:F2} | flags {flagTicks * perTick:F2} "
                  + $"| weapons {weaponTicks * perTick:F2} | grenades {grenadeTicks * perTick:F2} "
                  + $"| broadcast {broadcastTicks * perTick:F2} "
                  + $"| total {(aiTicks + actorTicks + flagTicks + weaponTicks + grenadeTicks + broadcastTicks) * perTick:F2} "
                  + $"| worst {worstMs:F1} ms | live projectiles {projectileSum / ticks}");

                windowStart = now;
                ticks = 0;
                aiTicks = actorTicks = flagTicks = weaponTicks = grenadeTicks = broadcastTicks = 0;
                projectileSum = 0;
                worstMs = 0;
            }
        }
        private ushort nextMobId = ActorIds.FirstMob;

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
            INetServer server,
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
            tickets = new TicketSystem(server, flags, playableTeams);
            terrainEdits = new TerrainSystem(server, terrain);
            grenades = new GrenadeSystem(
                objects,
                items,
                terrainEdits,
                terrain,
                activityFeed);
            mobs = new MobSystem(terrain, terrainEdits, weapons, items, flags, grenades);

            chunks = new ChunkTcpServer(terrain);
            chunks.Start();

            // Trees are temporarily disabled. TreeSystem and its client views remain available.
            // new TreeSystem(objects, terrain).SpawnInitialTrees();

            if (runtimeMap is null)
            {
                SpawnPickupOnSurface(ItemType.BodyArmor, 3f, 3f);
                SpawnPickupOnSurface(ItemType.AWP, 3f, 0f);
                SpawnPickupOnSurface(ItemType.Ak47, -3f, -3f);
                SpawnPickupOnSurface(ItemType.Sks, -3f, 0f);
                SpawnPickupOnSurface(ItemType.Ppsh, 0f, -3f);
                SpawnPickupOnSurface(ItemType.Mosin, 0f, 3f);
                SpawnPickupOnSurface(ItemType.Dp27, 3f, -3f);
                SpawnPickupOnSurface(ItemType.Shovel, -1.5f, 1.5f);
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
                var mob = SpawnMob(position, spawn.Team, weapon: null);
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
                    case RuntimePlacementKind.SupplyCrate:
                        items.SpawnSupplyCrate(placement.Item, placement.Position);
                        break;
                    case RuntimePlacementKind.Mob:
                        var mob = SpawnMob(placement.Position, placement.Team, placement.Item);
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
            => SpawnMob(requestedPosition, team: 1, weapon: null);

        private ServerPlayer SpawnMob(Vector3? requestedPosition, int team, ItemType? weapon)
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
            ItemType cohortWeapon = NextNpcPrimary(team);
            items.SpawnInfantryLoadout(
                mob,
                weapon is { } authored && WeaponConfig.Get(authored) is not null
                    ? authored
                    : cohortWeapon);
            server.SendToAll(CreateSpawnMessage(mob));
            return mob;
        }

        private ItemType NextNpcPrimary(int team)
        {
            int ordinal = npcSpawnOrdinalsByTeam.GetValueOrDefault(team);
            npcSpawnOrdinalsByTeam[team] = ordinal + 1;
            return NpcSquadLoadout.PrimaryForSpawnOrdinal(ordinal);
        }

        public ServerObject SpawnPickup(ItemType type, Vector3 position)
            => items.SpawnPickup(type, position);

        /// <summary>Flag ownership, for scenario setup in benchmarks. See FlagSystem.TryForceOwner.</summary>
        internal FlagSystem Flags => flags;

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
            for (int attempts = 0; attempts <= ushort.MaxValue - ActorIds.FirstMob; attempts++)
            {
                ushort candidate = nextMobId;
                nextMobId = candidate == ushort.MaxValue
                    ? ActorIds.FirstMob
                    : (ushort)(candidate + 1);
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
            tickets.SendTo(clientId);      // ...and on the score, which only moves every 3 s

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
            long tickStart = Stopwatch.GetTimestamp();
            mobs.BeginTick(_Tick, players.Values);
            long afterBeginTick = Stopwatch.GetTimestamp();
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
                    // The MOVE's hotbar, not the player's: the field is written when input arrives
                    // and the queue can be a tick or two behind, so stepping against it would apply
                    // a weight the client had not yet applied to that move — a correction on every
                    // weapon switch. The client replays from the same field.
                    PlayerMovement.Step(
                        terrain, ref player.Move, move.Intent, move.State, dt,
                        items.MoveSpeedScale(player, move.Hotbar));
                    player.State = move.State;
                    player.Yaw = move.Yaw;
                    player.Pitch = move.Pitch;
                    player.LastIntent = move.Intent;
                    player.LastProcessedSequence = move.Sequence;
                    processedAny = true;
                }

                // Queue starved, just reuse last player input
                if (!processedAny)
                    PlayerMovement.Step(
                        terrain, ref player.Move, player.LastIntent, player.State, dt,
                        items.MoveSpeedScale(player, player.Hotbar));
            }
            mobs.RecordTick(mobMovementTicks, mobCount);
            while (mobs.TryDequeueStuckMob(out ushort stuckMobId))
            {
                if (!players.TryGetValue(stuckMobId, out var stuck)
                    || !stuck.IsMob)
                    continue;
                activityFeed.ReportNpcRelocated(
                    stuckMobId,
                    "stuck for 60 seconds while navigating");
                Relocate(stuck);
            }

            long afterActors = Stopwatch.GetTimestamp();
            flags.Tick(dt, players.Values);
            // After the capture pass, so a flag that changed hands this tick is priced this tick.
            tickets.Tick(dt);

            // Save history
            foreach (var player in players.Values)
            {
                player.History.Store(_Tick, player.Position);
            }

            long afterFlags = Stopwatch.GetTimestamp();
            weapons.Tick(dt, _Tick, players.Values);
            long afterWeapons = Stopwatch.GetTimestamp();
            grenades.Tick(dt, _Tick, players.Values);
            RegenerateHealth(dt);
            long afterGrenades = Stopwatch.GetTimestamp();

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
                if (player.IsMob) mobs.OnRespawn(player);
                tickets.ChargeRespawn(player.Team);
                player.RespawnTick = 0;
            }

            objects.BroadcastDirtyStatess(_Tick);
            BroadcastPositions();

            if (_Tick % (NetworkConfig.TickRate * 2) == 0) mobs.LogStats();

            serverBreakdown.Record(
                tickStart,
                afterBeginTick,
                afterActors,
                afterFlags,
                afterWeapons,
                afterGrenades,
                Stopwatch.GetTimestamp(),
                projectiles: weapons.LiveProjectiles);
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

        /// <summary>
        /// Moves a wedged actor back to a spawn instead of removing it.
        ///
        /// Being stuck used to be fatal — permanently, since deletion does not respawn — so every
        /// piece of geometry an NPC could wedge itself in slowly drained the teams for the rest of
        /// the round. Getting stuck is a navigation failure, not a death: the fix is to pick the man
        /// up and put him somewhere he can walk from.
        ///
        /// Health is deliberately NOT restored. This is a relocation, not a respawn, and a wounded
        /// man who wedges himself should not come away healed.
        /// </summary>
        private void Relocate(ServerPlayer actor)
        {
            actor.Move = SpawnPlayerMove(actor.Team, useOverride: false);
            actor.History.Clear();
            actor.PendingMoves.Clear();
            actor.LastIntent = Vector3.Zero;
            actor.State = 0;

            // The brain still believes everything it believed while wedged — where it was going,
            // what it was fighting, that it was at cover. OnRespawn is exactly that wipe.
            mobs.OnRespawn(actor);
        }

        /// <summary>
        /// Closes wounds once an actor has been left alone long enough. Server-authoritative like
        /// every other health change, so the client's red-out and heartbeat follow the same number
        /// rather than predicting one of their own.
        ///
        /// The carry is what makes it work at all: the rate is 0.67 health per tick against a ushort
        /// field, so rounding each tick independently would heal precisely nothing.
        /// </summary>
        private void RegenerateHealth(float dt)
        {
            foreach (var player in players.Values)
            {
                if (player.Status is not { } status
                    || status.Health.Current == 0
                    || status.Health.Current >= status.Health.Max
                    || _Tick - player.LastDamagedTick < HealthConfig.RegenerationDelayTicks)
                {
                    player.RegenerationCarry = 0f;
                    continue;
                }

                player.RegenerationCarry += HealthConfig.RegenerationPerSecond * dt;
                int whole = (int)player.RegenerationCarry;
                if (whole <= 0) continue;

                player.RegenerationCarry -= whole;
                status.Health.Current = (ushort)Math.Min(
                    status.Health.Max,
                    status.Health.Current + whole);
                status.Dirty |= NetComponents.Health;
            }
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
