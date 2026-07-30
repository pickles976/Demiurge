using System.Diagnostics;
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Minimal server-side mob driver. Mobs are still ServerPlayers: this class only chooses intent,
    /// while movement, replication, health, equipped items, and weapon hit detection stay on the
    /// existing player/object systems.
    /// </summary>
    internal sealed class MobSystem : IDisposable
    {
        public const float RoamRadius = 50f;

        private const float ArriveDistance = 1.25f;
        private const float CoverArrivalDistance = 0.7f;
        private const float CornerArrivalDistance = 0.2f;
        private const int CoverRetryTicks = 2 * NetworkConfig.TickRate;
        private const int ConcealmentRequeryTicks = 3 * NetworkConfig.TickRate;
        private const int CoverCrouchTicks = 3 * NetworkConfig.TickRate / 4;
        private const int CoverStandTicks = NetworkConfig.TickRate;
        private const int CoverQueriesPerTick = 1;
        private const float CoverThreatRequeryDistance = 5f;
        private const float TurnRadiansPerSecond = 180f * MathF.PI / 180f;
        private const float ObjectiveFormationRadius = 1.75f;
        private const float ObjectiveHoldRadius = FlagConfig.CaptureRadius - 0.35f;
        private const float GoldenAngle = 2.39996323f;
        private const float IdleScanRadiansPerSecond = 45f * MathF.PI / 180f;

        private readonly ChunkMap terrain;
        private readonly TerrainSystem terrainEdits;
        private readonly WeaponSystem weapons;
        private readonly CommanderAi commander;
        private readonly NavigationSystem navigation;
        private readonly Perception perception;
        private readonly CombatBehavior combat;
        private readonly GrenadeBehavior grenadeCombat;
        private readonly CoverBehavior cover;
        private readonly Random random;
        private readonly Dictionary<ushort, Vector3> homes = new();
        private readonly Dictionary<ushort, MobBrain> brains = new();
        private readonly Dictionary<(int Team, int Squad), SquadBlackboard> squads = new();
        private readonly Queue<ushort> stuckMobs = new();
        private readonly Dictionary<int, int> assignedMembersByTeam = new();
        private int coverQueriesRemaining;
        private int timingTicks;
        private int timingAgentSamples;
        private long timingMovementStopwatchTicks;
        private long timingPerceptionStopwatchTicks;
        private long timingCoverStopwatchTicks;
        private int timingCoverQueries;
        private NavigationSystem.Metrics timingNavigationStart;
        private readonly List<long> timingNavigationQueueUs = [];
        private readonly List<long> timingNavigationSearchUs = [];
        private string latestStats = "AI stats are collecting their first 1-second window";

        public MobSystem(
            ChunkMap terrain,
            TerrainSystem terrainEdits,
            WeaponSystem weapons,
            FlagSystem flags,
            GrenadeSystem grenades,
            int seed = 0x51A7)
        {
            this.terrain = terrain;
            this.terrainEdits = terrainEdits;
            this.weapons = weapons;
            commander = new CommanderAi(flags);
            navigation = new NavigationSystem(terrain);
            perception = new Perception(terrain);
            combat = new CombatBehavior(weapons, terrain);
            grenadeCombat = new GrenadeBehavior(terrain, grenades);
            cover = new CoverBehavior(terrain);
            random = new Random(seed);
        }

        public Vector3 RandomSpawnPoint() => RandomSurfacePoint(Vector3.Zero);

        public ServerPlayer CreateMob(ushort id, Vector3 position, int team = 1)
        {
            team = team > 0 ? team : 1;
            var mob = new ServerPlayer
            {
                Id = id,
                IsMob = true,
                Team = team,
                Move = PlayerMovement.SpawnAt(terrain, position.X, position.Z),
            };
            homes[mob.Id] = mob.Position;
            brains[mob.Id] = CreateBrain(team, mob.Position);
            brains[mob.Id].Navigation.SetDestination(
                RandomSurfacePoint(mob.Position),
                clearPath: false);
            return mob;
        }

        public void BeginTick(uint tick, ICollection<ServerPlayer> actors)
        {
            coverQueriesRemaining = CoverQueriesPerTick;
            commander.Update(tick, squads, actors);
            ProcessGunshots(actors);
            foreach (var pair in squads)
            {
                var squad = pair.Value;
                squad.Advance(tick);
            }
            while (navigation.TryGetCompleted(out var result))
            {
                if (!brains.TryGetValue(result.MobId, out var brain))
                    continue;
                if (!brain.Navigation.TryCompleteRequest(
                        result.RequestId,
                        out bool forCover)
                    || forCover != brain.HasCoverDestination)
                    continue;

                if (result.Path.Waypoints.Count > 0
                    && NavPathTerrain.IsValid(terrain, result.Path))
                {
                    Vector3? currentPosition = actors
                        .FirstOrDefault(actor => actor.Id == result.MobId)
                        ?.Position;
                    brain.Navigation.Path.SetPath(
                        result.Path,
                        result.TerrainVersion,
                        currentPosition,
                        terrain);
                }
                else
                {
                    brain.Navigation.Path.Clear();
                    if (forCover)
                    {
                        ClearCover(result.MobId, brain, BoardFor(brain));
                        brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    }
                    else
                    {
                        var squad = BoardFor(brain);
                        brain.Navigation.SetDestination(
                            squad.TryGetObjective(out var objective)
                                ? ObjectiveDestination(result.MobId, objective.Position)
                                : RandomSurfacePoint(homes.GetValueOrDefault(result.MobId)));
                    }
                }
            }

            long started = Stopwatch.GetTimestamp();
            foreach (var actor in actors)
                if (actor.IsMob && actor.Status is not { Health.Current: 0 }
                    && brains.TryGetValue(actor.Id, out var brain))
                {
                    var squad = BoardFor(actor, brain);
                    squad.ShareContactsWith(brain.Contacts, tick);
                    if (perception.Tick(actor, actors, brain, tick) is { } observed)
                        squad.Publish(observed, tick);
                }
            timingPerceptionStopwatchTicks += Stopwatch.GetTimestamp() - started;
        }

        public void Step(
            ServerPlayer mob,
            float dt,
            uint tick,
            ICollection<ServerPlayer> actors)
        {
            if (!brains.TryGetValue(mob.Id, out var brain))
                brains[mob.Id] = brain = CreateBrain(mob.Team, mob.Position);
            var squad = BoardFor(mob, brain);
            bool hasObjective = squad.TryGetObjective(out var objective);
            if (brain.ObjectiveRevision != squad.ObjectiveRevision)
            {
                brain.ObjectiveRevision = squad.ObjectiveRevision;
                brain.Navigation.Path.Clear();
                brain.Navigation.ResetBlocked();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    hasObjective
                        ? ObjectiveDestination(mob.Id, objective.Position)
                        : RandomSurfacePoint(HomeOf(mob)));
            }
            if (!brain.Navigation.HasDestination)
                brain.Navigation.SetDestination(
                    hasObjective
                        ? ObjectiveDestination(mob.Id, objective.Position)
                        : RandomSurfacePoint(HomeOf(mob)));
            Vector3 destination = brain.Navigation.Destination;

            bool heardGunshot = brain.HasRecentGunshot(tick);
            if (heardGunshot
                && brain.AppliedHeardRevision != brain.HeardRevision)
            {
                brain.AppliedHeardRevision = brain.HeardRevision;
                brain.Navigation.Path.Clear();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    SurfaceQuery.SurfacePosition(
                        terrain,
                        brain.HeardPosition.X,
                        brain.HeardPosition.Z));
                destination = brain.Navigation.Destination;
            }
            else if (!heardGunshot && brain.AppliedHeardRevision != 0)
            {
                brain.ClearGunshot();
                brain.Navigation.Path.Clear();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    hasObjective
                        ? ObjectiveDestination(mob.Id, objective.Position)
                        : RandomSurfacePoint(HomeOf(mob)));
                destination = brain.Navigation.Destination;
            }

            if (grenadeCombat.TryThrow(mob, brain, squad, actors, tick))
            {
                brain.Navigation.Progress.Reset();
                mob.State = PlayerStateFlags.Shooting;
                mob.LastIntent = Vector3.Zero;
                PlayerMovement.Step(
                    terrain,
                    ref mob.Move,
                    Vector3.Zero,
                    mob.State,
                    dt);
                return;
            }
            mob.Hotbar = HotbarSlot.Primary;

            bool mayFire = squad.TryAcquireEngagement(mob.Id, tick);
            bool underFire = mob.Spread.SuppressionMoa > 1f;
            if (combat.Tick(mob, brain, tick, dt, mayFire && !underFire))
            {
                brain.Navigation.Progress.Reset();
                bool wantsAdvance = brain.ShouldCloseDistance || !mayFire || underFire;
                bool mayAdvance =
                    underFire
                    || wantsAdvance && squad.TryAcquireAdvance(mob.Id, tick);
                if (underFire || !mayAdvance)
                    squad.ReleaseAdvance(mob.Id);
                UpdateCoverMovement(
                    mob,
                    brain,
                    squad,
                    mayAdvance,
                    brain.ShouldCloseDistance,
                    underFire,
                    tick,
                    out var combatIntent,
                    out bool combatJump);
                bool crouching = brain.AtCover
                    && (mob.State.HasFlag(PlayerStateFlags.Reloading)
                        || ShouldCrouchAtCover(brain, tick));
                mob.State = mob.State
                    .With(PlayerStateFlags.Moving, combatIntent != Vector3.Zero)
                    .With(PlayerStateFlags.Jumping, combatJump)
                    .With(PlayerStateFlags.Crouching, crouching);
                mob.LastIntent = combatIntent;
                PlayerMovement.Step(
                    terrain,
                    ref mob.Move,
                    combatIntent,
                    mob.State,
                    dt);
                return;
            }
            squad.ReleaseEngagement(mob.Id);
            squad.ReleaseAdvance(mob.Id);
            if (brain.HasCoverDestination)
            {
                ClearCover(mob.Id, brain, squad);
                CancelPending(mob.Id, brain.Navigation, forCover: true);
            }
            var follower = brain.Navigation.Path;
            bool holdingObjective =
                hasObjective
                && !heardGunshot
                && HorizontalDistanceSquared(mob.Position, objective.Position)
                    <= ObjectiveHoldRadius * ObjectiveHoldRadius;
            PathFollowState followState;
            Vector3 intent;
            bool jump;
            bool digging = false;
            bool terrainProgress = false;
            if (holdingObjective)
            {
                follower.Clear();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                followState = PathFollowState.Following;
                intent = Vector3.Zero;
                jump = false;
            }
            else
            {
                followState = follower.Update(
                    mob.Position,
                    mob.Move.Grounded,
                    terrain.EditVersion,
                    out intent,
                    out jump,
                    out var digTarget,
                    out var blockedCell);
                brain.Navigation.RememberBlocked(blockedCell);
                if (followState == PathFollowState.Digging)
                {
                    digging = true;
                    intent = Vector3.Zero;
                    jump = false;
                    mob.Hotbar = HotbarSlot.Shovel;
                    FaceDigTarget(mob, digTarget, dt);
                    long versionBeforeDig = terrain.EditVersion;
                    terrainEdits.ApplyDig(
                        mob,
                        new PlayerDigData
                        {
                            Target = digTarget,
                            Hotbar = HotbarSlot.Shovel,
                        },
                        tick);
                    terrainProgress = terrain.EditVersion != versionBeforeDig;
                }
                if (followState == PathFollowState.Complete)
                {
                    bool reachedGoal = follower.ReachedGoal;
                    follower.Clear();
                    if (reachedGoal && heardGunshot)
                    {
                        brain.ClearGunshot();
                        heardGunshot = false;
                        brain.Navigation.SetDestination(
                            hasObjective
                                ? ObjectiveDestination(mob.Id, objective.Position)
                                : RandomSurfacePoint(HomeOf(mob)));
                        destination = brain.Navigation.Destination;
                        followState = PathFollowState.NeedsPath;
                    }
                    else if (reachedGoal && hasObjective)
                    {
                        followState = PathFollowState.Following;
                    }
                    else
                    {
                        if (reachedGoal)
                        {
                            brain.Navigation.SetDestination(
                                RandomSurfacePoint(HomeOf(mob)));
                            destination = brain.Navigation.Destination;
                        }
                        followState = PathFollowState.NeedsPath;
                    }
                }
            }

            if (followState == PathFollowState.NeedsPath)
            {
                intent = Vector3.Zero;
                jump = false;
                bool requested = RequestPath(
                    mob,
                    destination,
                    forCover: false,
                    allowDig: true,
                    priority: NavigationPriority.MissingPath);
                if (!requested && heardGunshot)
                {
                    brain.ClearGunshot();
                    heardGunshot = false;
                    brain.Navigation.SetDestination(
                        hasObjective
                            ? ObjectiveDestination(mob.Id, objective.Position)
                            : RandomSurfacePoint(HomeOf(mob)));
                }
                else if (!requested && !hasObjective)
                {
                    brain.Navigation.SetDestination(RandomSurfacePoint(HomeOf(mob)));
                }
            }
            else if (followState == PathFollowState.Following
                     && follower.ShouldRefreshPath)
            {
                // Partial A* results are deliberately bounded, but the next segment can be
                // requested before this one ends. The NPC keeps following its current waypoints
                // while the single background worker plans, avoiding walk/wait/walk oscillation.
                RequestPath(
                    mob,
                    destination,
                    forCover: false,
                    allowDig: true,
                    priority: NavigationPriority.Prefetch);
            }

            mob.State = PlayerStateFlags.None
                .With(PlayerStateFlags.Moving, intent != Vector3.Zero)
                .With(PlayerStateFlags.Jumping, jump);
            if (intent != Vector3.Zero)
                mob.Yaw = RotateYawTowards(
                    mob.Yaw,
                    MathF.Atan2(intent.X, intent.Z),
                    TurnRadiansPerSecond * dt);
            else if (!digging && heardGunshot)
                FaceHorizontalTarget(mob, brain.HeardPosition, dt);
            else if (!digging && holdingObjective)
                mob.Yaw = NormalizeRadians(
                    mob.Yaw + IdleScanRadiansPerSecond * dt);
            if (!digging)
                mob.Pitch = 0f;
            mob.LastIntent = intent;

            PlayerMovement.Step(terrain, ref mob.Move, intent, mob.State, dt);
            if (brain.Navigation.Progress.Update(
                    mob.Position,
                    tick,
                    expectedToTravel: !holdingObjective,
                    terrainProgress))
                stuckMobs.Enqueue(mob.Id);
        }

        public void Dispose() => navigation.Dispose();

        public bool TryDequeueStuckMob(out ushort mobId)
            => stuckMobs.TryDequeue(out mobId);

        public void RemoveMob(ServerPlayer mob)
        {
            navigation.Cancel(mob.Id);
            if (brains.Remove(mob.Id, out var brain))
            {
                var squad = BoardFor(brain);
                squad.RemoveMember(mob.Id, HomeOf(mob));
                brain.Navigation.Clear();
            }
            homes.Remove(mob.Id);
        }

        private void CancelPending(
            ushort mobId,
            NavigationAgent agent,
            bool? forCover = null)
        {
            if (agent.CancelPending(forCover) is { } requestId)
                navigation.Cancel(mobId, requestId);
        }

        private void UpdateCoverMovement(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            bool mayAdvance,
            bool closingDistance,
            bool underFire,
            uint tick,
            out Vector3 intent,
            out bool jump)
        {
            intent = Vector3.Zero;
            jump = false;

            if (!brain.Contacts.TryGet(brain.CombatTargetId, tick, out var primaryThreat))
                return;

            bool invalidated =
                brain.HasCoverDestination
                && (brain.CoverThreatId != primaryThreat.ActorId
                    || Vector3.DistanceSquared(
                        brain.CoverThreatPosition,
                        primaryThreat.Position)
                    > CoverThreatRequeryDistance * CoverThreatRequeryDistance
                    || brain.CoverTerrainVersion != terrain.EditVersion
                    || brain.CoverKind == CoverKind.Advance && !closingDistance);
            if (invalidated)
            {
                ClearCover(mob.Id, brain, squad);
                brain.NextCoverQueryTick = tick;
            }

            bool reconsiderConcealment =
                brain.AtCover
                && (brain.CoverKind == CoverKind.Concealment
                    || closingDistance)
                && tick >= brain.NextCoverQueryTick;
            if (mayAdvance
                && (!brain.HasCoverDestination || reconsiderConcealment)
                && tick >= brain.NextCoverQueryTick
                && coverQueriesRemaining > 0)
            {
                coverQueriesRemaining--;
                var contacts = brain.Contacts.Snapshot(tick);
                long coverStarted = Stopwatch.GetTimestamp();
                bool foundCover = cover.TryChoose(
                    mob,
                    contacts,
                    squad,
                    out var choice,
                    requireAdvance: closingDistance);
                timingCoverStopwatchTicks += Stopwatch.GetTimestamp() - coverStarted;
                timingCoverQueries++;
                if (foundCover)
                {
                    ClearCover(mob.Id, brain, squad);
                    if (!squad.TryClaim(mob.Id, choice.Position, tick))
                    {
                        brain.NextCoverQueryTick = tick + CoverRetryTicks;
                        return;
                    }
                    brain.HasCoverDestination = true;
                    brain.CoverDestination = choice.Position;
                    brain.CoverPeekPosition = choice.PeekPosition;
                    brain.CoverKind = choice.Kind;
                    brain.CoverThreatId = primaryThreat.ActorId;
                    brain.CoverThreatPosition = primaryThreat.Position;
                    brain.CoverTerrainVersion = choice.TerrainVersion;
                    brain.NextCoverQueryTick = tick + (uint)(choice.Kind == CoverKind.Concealment
                        ? ConcealmentRequeryTicks
                        : CoverRetryTicks);

                    if (HorizontalDistanceSquared(mob.Position, choice.Position)
                        <= CoverArrivalDistance * CoverArrivalDistance)
                    {
                        brain.AtCover = true;
                        brain.CoverArrivedTick = tick;
                    }
                    else
                    {
                        RequestPath(
                            mob,
                            choice.Position,
                            forCover: true,
                            replacePending: true);
                    }
                }
                else if (closingDistance
                         && TryCombatAdvancePosition(
                             mob,
                             primaryThreat.Position,
                             out var advance))
                {
                    ClearCover(mob.Id, brain, squad);
                    if (!squad.TryClaim(mob.Id, advance, tick))
                    {
                        brain.NextCoverQueryTick = tick + CoverRetryTicks;
                        return;
                    }
                    brain.HasCoverDestination = true;
                    brain.CoverDestination = advance;
                    brain.CoverPeekPosition = advance;
                    brain.CoverKind = CoverKind.Advance;
                    brain.CoverThreatId = primaryThreat.ActorId;
                    brain.CoverThreatPosition = primaryThreat.Position;
                    brain.CoverTerrainVersion = terrain.EditVersion;
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    RequestPath(
                        mob,
                        advance,
                        forCover: true,
                        replacePending: true,
                        allowJump: true);
                }
                else
                {
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    if (underFire)
                        DigEmergencyCover(mob, primaryThreat.Position, tick);
                }
            }

            if (!brain.HasCoverDestination)
                return;
            squad.RefreshClaim(mob.Id, tick);
            if (brain.AtCover)
            {
                if (brain.CoverKind == CoverKind.CornerFightingPosition)
                {
                    bool tucked = mob.State.HasFlag(PlayerStateFlags.Reloading)
                        || ShouldCrouchAtCover(brain, tick);
                    Vector3 desired = tucked
                        ? brain.CoverDestination
                        : brain.CoverPeekPosition;
                    Vector3 delta = desired - mob.Position;
                    delta.Y = 0f;
                    if (delta.LengthSquared() > CornerArrivalDistance * CornerArrivalDistance)
                        intent = Vector3.Normalize(delta);
                }
                return;
            }
            if (!mayAdvance)
                return;

            var followState = brain.Navigation.Path.Update(
                mob.Position,
                mob.Move.Grounded,
                terrain.EditVersion,
                out intent,
                out jump,
                out _,
                out var blockedCell);
            brain.Navigation.RememberBlocked(blockedCell);
            if (followState == PathFollowState.Complete)
            {
                bool arrived = brain.Navigation.Path.ReachedGoal
                    || HorizontalDistanceSquared(mob.Position, brain.CoverDestination)
                    <= CoverArrivalDistance * CoverArrivalDistance;
                brain.Navigation.Path.Clear();
                intent = Vector3.Zero;
                jump = false;
                if (arrived)
                {
                    brain.AtCover = true;
                    brain.CoverArrivedTick = tick;
                    if (brain.CoverKind == CoverKind.Concealment)
                        brain.NextCoverQueryTick = tick + ConcealmentRequeryTicks;
                }
                else
                {
                    ClearCover(mob.Id, brain, squad);
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
                }
            }
            else if (followState == PathFollowState.NeedsPath)
            {
                intent = Vector3.Zero;
                jump = false;
                RequestPath(
                    mob,
                    brain.CoverDestination,
                    forCover: true);
            }
        }

        private static bool ShouldCrouchAtCover(MobBrain brain, uint tick)
        {
            if (brain.CoverKind == CoverKind.Concealment) return true;
            uint elapsed = tick - brain.CoverArrivedTick;
            uint cycle = CoverCrouchTicks + CoverStandTicks;
            return elapsed % cycle < CoverCrouchTicks;
        }

        private void DigEmergencyCover(
            ServerPlayer mob,
            Vector3 threatPosition,
            uint tick)
        {
            if (tick < mob.NextDigTick) return;

            Vector3 toward = threatPosition - mob.Position;
            toward.Y = 0f;
            if (toward.LengthSquared() > 1e-6f)
                toward = Vector3.Normalize(toward);

            Vector3 probe = mob.Position + toward * 0.55f + Vector3.UnitY * 0.6f;
            if (TerrainRaycast.Cast(
                    terrain,
                    probe,
                    -Vector3.UnitY,
                    1.5f) is not { } ground)
                return;

            Vector3 target = Digging.TargetVoxel(ground.Point, ground.Normal);
            if (!terrain.TryGetVoxel(
                    (int)MathF.Round(target.X),
                    (int)MathF.Round(target.Y),
                    (int)MathF.Round(target.Z),
                    out var voxel)
                || voxel.Distance >= 0f
                || voxel.Material is not (
                    BlockType.BlockType_Dirt
                    or BlockType.BlockType_Grass))
                return;

            mob.Hotbar = HotbarSlot.Shovel;
            mob.Yaw = MathF.Atan2(toward.X, toward.Z);
            mob.Pitch = -MathF.PI * 0.35f;
            terrainEdits.ApplyDig(
                mob,
                new PlayerDigData
                {
                    Target = target,
                    Hotbar = HotbarSlot.Shovel,
                },
                tick);
        }

        public void RecordTick(long movementStopwatchTicks, int agentCount)
        {
            if (timingTicks == 0)
                timingNavigationStart = navigation.SnapshotMetrics();

            timingMovementStopwatchTicks += movementStopwatchTicks;
            timingAgentSamples += agentCount;
            timingTicks++;
            navigation.DrainTimingSamples(
                timingNavigationQueueUs,
                timingNavigationSearchUs);
            if (timingTicks < NetworkConfig.TickRate) return;

            var nav = navigation.SnapshotMetrics();
            long requests = nav.Requested - timingNavigationStart.Requested;
            long completed = nav.Completed - timingNavigationStart.Completed;
            long pathUs = nav.SearchMicroseconds - timingNavigationStart.SearchMicroseconds;
            long queueUs = nav.QueueMicroseconds - timingNavigationStart.QueueMicroseconds;
            long expanded = nav.ExpandedNodes - timingNavigationStart.ExpandedNodes;
            long metres = nav.ReturnedPathMetres - timingNavigationStart.ReturnedPathMetres;
            long cacheHits = nav.CacheHits - timingNavigationStart.CacheHits;
            long partial = nav.PartialPaths - timingNavigationStart.PartialPaths;
            long complete = nav.CompletePaths - timingNavigationStart.CompletePaths;
            long cancelled = nav.Cancelled - timingNavigationStart.Cancelled;
            long invalidated =
                nav.SpatialInvalidations - timingNavigationStart.SpatialInvalidations;
            long sharedReuses =
                nav.SharedRouteReuses - timingNavigationStart.SharedRouteReuses;
            double movementUsPerTick =
                timingMovementStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double perceptionUsPerTick =
                timingPerceptionStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double coverUsPerTick =
                timingCoverStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double pathUsPerTick = pathUs / (double)timingTicks;
            double queueUsPerPath = completed == 0 ? 0d : queueUs / (double)completed;
            long queueP50 = Percentile(timingNavigationQueueUs, 0.50f);
            long queueP95 = Percentile(timingNavigationQueueUs, 0.95f);
            long searchP50 = Percentile(timingNavigationSearchUs, 0.50f);
            long searchP95 = Percentile(timingNavigationSearchUs, 0.95f);
            double nodesPerPath = completed == 0 ? 0d : expanded / (double)completed;
            double metresPerPath =
                partial + complete == 0 ? 0d : metres / (double)(partial + complete);
            double agents = timingAgentSamples / (double)timingTicks;

            latestStats = FormattableString.Invariant(
                $"AI 1s avg: agents {agents:0.0}; movement {movementUsPerTick:0.0} us/tick; perception {perceptionUsPerTick:0.0} us/tick; cover {coverUsPerTick:0.0} us/tick ({timingCoverQueries} queries); {navigation.WorkerCount} path workers {pathUsPerTick:0.0} aggregate us/tick off-thread; paths {requests} requested, {completed} completed ({complete} full/{partial} partial), queue {queueUsPerPath:0} us/path p50/p95 {queueP50}/{queueP95} us, search p50/p95 {searchP50}/{searchP95} us, {nodesPerPath:0} nodes/path, {metresPerPath:0.0} m/path, traversal cache {cacheHits} hits, shared routes {sharedReuses}, {cancelled} cancelled, {invalidated} spatially invalidated");

            timingTicks = 0;
            timingAgentSamples = 0;
            timingMovementStopwatchTicks = 0;
            timingPerceptionStopwatchTicks = 0;
            timingCoverStopwatchTicks = 0;
            timingCoverQueries = 0;
            timingNavigationQueueUs.Clear();
            timingNavigationSearchUs.Clear();
        }

        public string Stats() => latestStats;

        private static long Percentile(List<long> samples, float percentile)
        {
            if (samples.Count == 0) return 0;
            samples.Sort();
            int index = Math.Clamp(
                (int)MathF.Ceiling(samples.Count * percentile) - 1,
                0,
                samples.Count - 1);
            return samples[index];
        }

        private Vector3 HomeOf(ServerPlayer mob)
            => homes.TryGetValue(mob.Id, out var home) ? home : mob.Position;

        private MobBrain CreateBrain(int team, Vector3 home)
        {
            int assigned = assignedMembersByTeam.GetValueOrDefault(team);
            assignedMembersByTeam[team] = assigned + 1;
            int squadIndex = assigned / SquadBlackboard.MaximumMembers;
            _ = squads.TryAdd((team, squadIndex), new SquadBlackboard());
            squads[(team, squadIndex)].AddMemberHome(home);
            return new MobBrain { Team = team, SquadIndex = squadIndex };
        }

        private SquadBlackboard BoardFor(ServerPlayer mob, MobBrain brain)
        {
            var key = (mob.Team, brain.SquadIndex);
            if (!squads.TryGetValue(key, out var squad))
                squads[key] = squad = new SquadBlackboard();
            return squad;
        }

        private SquadBlackboard BoardFor(MobBrain brain)
        {
            var key = (brain.Team, brain.SquadIndex);
            if (!squads.TryGetValue(key, out var squad))
                squads[key] = squad = new SquadBlackboard();
            return squad;
        }

        private static void ClearCover(
            ushort mobId,
            MobBrain brain,
            SquadBlackboard squad)
        {
            squad.ReleaseClaim(mobId);
            brain.ClearCover();
        }

        private bool RequestPath(
            ServerPlayer mob,
            Vector3 destination,
            bool forCover,
            bool replacePending = false,
            bool allowDig = false,
            bool allowJump = false,
            NavigationPriority priority = NavigationPriority.Objective)
        {
            var navigationAgent = brains[mob.Id].Navigation;
            if (navigationAgent.HasPending(forCover) && !replacePending)
                return true;
            if (!TryCellAt(mob.Position, out var start)
                || !TryCellAt(destination, out var target))
                return false;
            long? blockedCellKey = navigationAgent.TakeAvoidedCell();
            long sharedRouteKey = 0;
            var brain = brains[mob.Id];
            if (!forCover
                && BoardFor(brain).TryGetObjective(out var objective)
                && HorizontalDistanceSquared(destination, objective.Position)
                    <= FlagConfig.CaptureRadius * FlagConfig.CaptureRadius)
            {
                sharedRouteKey =
                    ((long)(uint)brain.Team << 48)
                    ^ ((long)(uint)brain.SquadIndex << 32)
                    ^ objective.FlagId;
            }

            long requestId = navigation.Request(
                mob.Id,
                start,
                new GoalNear(target, forCover ? CoverArrivalDistance : ArriveDistance),
                allowJump: !forCover || allowJump,
                allowDig: allowDig && !forCover,
                blockedCellKey: blockedCellKey,
                sharedRouteKey: sharedRouteKey,
                priority: forCover ? NavigationPriority.Combat : priority);
            if (requestId != 0)
                navigationAgent.RecordRequest(requestId, forCover);
            return requestId != 0;
        }

        private bool TryCellAt(Vector3 position, out NavCell cell)
            => NavTraversal.TryFindNearestStandable(
                terrain,
                position,
                horizontalRadius: 3,
                out cell);

        private Vector3 RandomSurfacePoint(Vector3 center)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                float angle = (float)(random.NextDouble() * MathF.Tau);
                float distance = MathF.Sqrt((float)random.NextDouble()) * RoamRadius;
                float x = center.X + MathF.Cos(angle) * distance;
                float z = center.Z + MathF.Sin(angle) * distance;
                var chunk = ChunkTransforms.ChunkAt((int)MathF.Floor(x), (int)MathF.Floor(z));
                if (chunk.x < WorldGen.MeshableMin.x || chunk.x > WorldGen.MeshableMax.x
                    || chunk.z < WorldGen.MeshableMin.z || chunk.z > WorldGen.MeshableMax.z)
                    continue;
                return SurfaceQuery.SurfacePosition(terrain, x, z);
            }

            return SurfaceQuery.SurfacePosition(terrain, center.X, center.Z);
        }

        private Vector3 ObjectiveDestination(ushort mobId, Vector3 centre)
        {
            float angle = mobId * GoldenAngle;
            float x = centre.X + MathF.Cos(angle) * ObjectiveFormationRadius;
            float z = centre.Z + MathF.Sin(angle) * ObjectiveFormationRadius;
            return SurfaceQuery.SurfacePosition(terrain, x, z);
        }

        private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        private bool TryCombatAdvancePosition(
            ServerPlayer mob,
            Vector3 threat,
            out Vector3 position)
        {
            position = default;
            Vector3 toward = threat - mob.Position;
            toward.Y = 0f;
            float range = toward.Length();
            if (range <= CombatBehavior.PreferredEngagementRange + 1f)
                return false;

            toward /= range;
            float travel = MathF.Min(
                8f,
                range - CombatBehavior.PreferredEngagementRange);
            Vector3 sample = mob.Position + toward * travel;
            if (!TryCellAt(sample, out var cell))
                return false;
            position = NavTraversal.Position(terrain, cell);
            return true;
        }

        private static float RotateYawTowards(
            float current,
            float target,
            float maximumRadians)
        {
            float delta = MathF.Atan2(
                MathF.Sin(target - current),
                MathF.Cos(target - current));
            return current + Math.Clamp(delta, -maximumRadians, maximumRadians);
        }

        private static void FaceDigTarget(ServerPlayer mob, Vector3 target, float dt)
        {
            Vector3 delta = target - Digging.Eye(mob.Position);
            float horizontal = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            if (horizontal <= 1e-5f) return;

            mob.Yaw = RotateYawTowards(
                mob.Yaw,
                MathF.Atan2(delta.X, delta.Z),
                TurnRadiansPerSecond * dt);
            mob.Pitch = MathF.Atan2(delta.Y, horizontal);
        }

        private void ProcessGunshots(ICollection<ServerPlayer> actors)
        {
            while (weapons.TryDequeueGunshot(out var shot))
                foreach (var listener in actors)
                {
                    if (!listener.IsMob
                        || listener.Id == shot.ShooterId
                        || listener.Status is not { Health.Current: > 0 }
                        || !GunshotHearing.CanHear(
                            listener.Team,
                            listener.Position,
                            shot.ShooterTeam,
                            shot.Position)
                        || !brains.TryGetValue(listener.Id, out var brain)
                        || shot.Tick < brain.HeardTick)
                        continue;

                    bool newInvestigation =
                        brain.HeardActorId != shot.ShooterId
                        || Vector3.DistanceSquared(
                            brain.HeardPosition,
                            shot.Position) > 4f * 4f;
                    if (newInvestigation)
                    {
                        brain.HeardActorId = shot.ShooterId;
                        brain.HeardPosition = shot.Position;
                        brain.HeardRevision++;
                        if (brain.HeardRevision == 0)
                            brain.HeardRevision = 1;
                    }
                    brain.HeardTick = shot.Tick;
                }
        }

        private static void FaceHorizontalTarget(
            ServerPlayer mob,
            Vector3 target,
            float dt)
        {
            Vector3 delta = target - mob.Position;
            delta.Y = 0f;
            if (delta.LengthSquared() <= 1e-6f) return;
            mob.Yaw = RotateYawTowards(
                mob.Yaw,
                MathF.Atan2(delta.X, delta.Z),
                TurnRadiansPerSecond * dt);
        }

        private static float NormalizeRadians(float angle)
            => MathF.Atan2(MathF.Sin(angle), MathF.Cos(angle));

    }
}
