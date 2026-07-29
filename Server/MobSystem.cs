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

        private readonly record struct PendingPath(long RequestId, bool ForCover);

        private readonly ChunkMap terrain;
        private readonly FlagSystem flags;
        private readonly NavigationSystem navigation;
        private readonly Perception perception;
        private readonly CombatBehavior combat;
        private readonly GrenadeBehavior grenadeCombat;
        private readonly CoverBehavior cover;
        private readonly Random random;
        private readonly Dictionary<ushort, Vector3> destinations = new();
        private readonly Dictionary<ushort, Vector3> homes = new();
        private readonly Dictionary<ushort, MobBrain> brains = new();
        private readonly Dictionary<(int Team, int Squad), SquadBlackboard> squads = new();
        private readonly Dictionary<int, int> assignedMembersByTeam = new();
        private readonly Dictionary<ushort, PendingPath> pendingRequests = new();
        private int coverQueriesRemaining;
        private int timingTicks;
        private int timingAgentSamples;
        private long timingMovementStopwatchTicks;
        private long timingPerceptionStopwatchTicks;
        private long timingCoverStopwatchTicks;
        private int timingCoverQueries;
        private NavigationSystem.Metrics timingNavigationStart;
        private string latestStats = "AI stats are collecting their first 1-second window";

        public MobSystem(
            ChunkMap terrain,
            WeaponSystem weapons,
            FlagSystem flags,
            GrenadeSystem grenades,
            int seed = 0x51A7)
        {
            this.terrain = terrain;
            this.flags = flags;
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
            destinations[mob.Id] = RandomSurfacePoint(mob.Position);
            brains[mob.Id] = CreateBrain(team, mob.Position);
            return mob;
        }

        public void BeginTick(uint tick, ICollection<ServerPlayer> actors)
        {
            coverQueriesRemaining = CoverQueriesPerTick;
            foreach (var pair in squads)
            {
                var squad = pair.Value;
                uint currentFlagId = squad.TryGetObjective(out var currentObjective)
                    ? currentObjective.FlagId
                    : 0;
                squad.SetObjective(
                    flags.TryGetSquadObjective(
                        pair.Key.Team,
                        squad.Home,
                        currentFlagId,
                        out var objective)
                        ? objective
                        : null);
                squad.Advance(tick);
            }
            while (navigation.TryGetCompleted(out var result))
            {
                if (!pendingRequests.TryGetValue(result.MobId, out var expected)
                    || expected.RequestId != result.RequestId)
                    continue;

                pendingRequests.Remove(result.MobId);
                if (!brains.TryGetValue(result.MobId, out var brain))
                    continue;
                if (expected.ForCover != brain.HasCoverDestination)
                    continue;

                if (result.TerrainVersion == terrain.EditVersion
                    && result.Path.Waypoints.Count > 0)
                {
                    brain.Path.SetPath(result.Path, result.TerrainVersion);
                }
                else
                {
                    brain.Path.Clear();
                    if (expected.ForCover)
                    {
                        ClearCover(result.MobId, brain, BoardFor(brain));
                        brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    }
                    else
                    {
                        var squad = BoardFor(brain);
                        destinations[result.MobId] =
                            squad.TryGetObjective(out var objective)
                                ? ObjectiveDestination(result.MobId, objective.Position)
                                : RandomSurfacePoint(homes.GetValueOrDefault(result.MobId));
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
                brain.Path.Clear();
                if (pendingRequests.TryGetValue(mob.Id, out var pending)
                    && !pending.ForCover)
                    pendingRequests.Remove(mob.Id);
                destinations[mob.Id] = hasObjective
                    ? ObjectiveDestination(mob.Id, objective.Position)
                    : RandomSurfacePoint(HomeOf(mob));
            }
            if (!destinations.TryGetValue(mob.Id, out var destination))
                destination = destinations[mob.Id] = hasObjective
                    ? ObjectiveDestination(mob.Id, objective.Position)
                    : RandomSurfacePoint(HomeOf(mob));

            if (grenadeCombat.TryThrow(mob, brain, squad, actors, tick))
            {
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
            bool mayAdvance = !mayFire && squad.TryAcquireAdvance(mob.Id, tick);
            if (mayFire)
                squad.ReleaseAdvance(mob.Id);
            if (combat.Tick(mob, brain, tick, dt, mayFire))
            {
                UpdateCoverMovement(
                    mob,
                    brain,
                    squad,
                    mayAdvance,
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
                if (pendingRequests.TryGetValue(mob.Id, out var pending) && pending.ForCover)
                    pendingRequests.Remove(mob.Id);
            }
            var follower = brain.Path;
            bool holdingObjective =
                hasObjective
                && HorizontalDistanceSquared(mob.Position, objective.Position)
                    <= ObjectiveHoldRadius * ObjectiveHoldRadius;
            PathFollowState followState;
            Vector3 intent;
            bool jump;
            if (holdingObjective)
            {
                follower.Clear();
                if (pendingRequests.TryGetValue(mob.Id, out var pending)
                    && !pending.ForCover)
                    pendingRequests.Remove(mob.Id);
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
                    out jump);
                if (followState == PathFollowState.Complete)
                {
                    bool reachedGoal = follower.ReachedGoal;
                    follower.Clear();
                    if (reachedGoal && hasObjective)
                    {
                        followState = PathFollowState.Following;
                    }
                    else
                    {
                        if (reachedGoal)
                            destination = destinations[mob.Id] =
                                RandomSurfacePoint(HomeOf(mob));
                        followState = PathFollowState.NeedsPath;
                    }
                }
            }

            if (followState == PathFollowState.NeedsPath)
            {
                intent = Vector3.Zero;
                jump = false;
                RequestPath(mob, destination, forCover: false);
            }

            mob.State = PlayerStateFlags.None
                .With(PlayerStateFlags.Moving, intent != Vector3.Zero)
                .With(PlayerStateFlags.Jumping, jump);
            if (intent != Vector3.Zero)
                mob.Yaw = RotateYawTowards(
                    mob.Yaw,
                    MathF.Atan2(intent.X, intent.Z),
                    TurnRadiansPerSecond * dt);
            mob.Pitch = 0f;
            mob.LastIntent = intent;

            PlayerMovement.Step(terrain, ref mob.Move, intent, mob.State, dt);
        }

        public void Dispose() => navigation.Dispose();

        private void UpdateCoverMovement(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            bool mayAdvance,
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
                    || brain.CoverTerrainVersion != terrain.EditVersion);
            if (invalidated)
            {
                ClearCover(mob.Id, brain, squad);
                brain.NextCoverQueryTick = tick;
            }

            bool reconsiderConcealment =
                brain.AtCover
                && brain.CoverKind == CoverKind.Concealment
                && tick >= brain.NextCoverQueryTick;
            if (mayAdvance
                && (!brain.HasCoverDestination || reconsiderConcealment)
                && tick >= brain.NextCoverQueryTick
                && coverQueriesRemaining > 0)
            {
                coverQueriesRemaining--;
                var contacts = brain.Contacts.Snapshot(tick);
                long coverStarted = Stopwatch.GetTimestamp();
                bool foundCover = cover.TryChoose(mob, contacts, squad, out var choice);
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
                else
                {
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
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

            var followState = brain.Path.Update(
                mob.Position,
                mob.Move.Grounded,
                terrain.EditVersion,
                out intent,
                out jump);
            if (followState == PathFollowState.Complete)
            {
                bool arrived = brain.Path.ReachedGoal
                    || HorizontalDistanceSquared(mob.Position, brain.CoverDestination)
                    <= CoverArrivalDistance * CoverArrivalDistance;
                brain.Path.Clear();
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

        public void RecordTick(long movementStopwatchTicks, int agentCount)
        {
            if (timingTicks == 0)
                timingNavigationStart = navigation.SnapshotMetrics();

            timingMovementStopwatchTicks += movementStopwatchTicks;
            timingAgentSamples += agentCount;
            timingTicks++;
            if (timingTicks < NetworkConfig.TickRate) return;

            var nav = navigation.SnapshotMetrics();
            long requests = nav.Requested - timingNavigationStart.Requested;
            long completed = nav.Completed - timingNavigationStart.Completed;
            long pathUs = nav.SearchMicroseconds - timingNavigationStart.SearchMicroseconds;
            double movementUsPerTick =
                timingMovementStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double perceptionUsPerTick =
                timingPerceptionStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double coverUsPerTick =
                timingCoverStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double pathUsPerTick = pathUs / (double)timingTicks;
            double agents = timingAgentSamples / (double)timingTicks;

            latestStats = FormattableString.Invariant(
                $"AI 1s avg: agents {agents:0.0}; movement {movementUsPerTick:0.0} us/tick; perception {perceptionUsPerTick:0.0} us/tick; cover {coverUsPerTick:0.0} us/tick ({timingCoverQueries} queries); path worker {pathUsPerTick:0.0} us/tick off-thread; paths {requests} requested, {completed} completed");

            timingTicks = 0;
            timingAgentSamples = 0;
            timingMovementStopwatchTicks = 0;
            timingPerceptionStopwatchTicks = 0;
            timingCoverStopwatchTicks = 0;
            timingCoverQueries = 0;
        }

        public string Stats() => latestStats;

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

        private void RequestPath(
            ServerPlayer mob,
            Vector3 destination,
            bool forCover,
            bool replacePending = false)
        {
            if (pendingRequests.TryGetValue(mob.Id, out var pending)
                && !replacePending
                && pending.ForCover == forCover)
                return;
            if (!TryCellAt(mob.Position, out var start)
                || !TryCellAt(destination, out var target))
                return;

            long requestId = navigation.Request(
                mob.Id,
                start,
                new GoalNear(target, forCover ? CoverArrivalDistance : ArriveDistance),
                allowJump: !forCover);
            if (requestId != 0)
                pendingRequests[mob.Id] = new PendingPath(requestId, forCover);
        }

        private bool TryCellAt(Vector3 position, out NavCell cell)
            => NavTraversal.TryFindStandable(
                terrain,
                (int)MathF.Floor(position.X),
                (int)MathF.Floor(position.Z),
                (int)MathF.Floor(position.Y),
                below: 3,
                above: 3,
                out cell,
                out _);

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
    }
}
