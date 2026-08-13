using System.Diagnostics;
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Chooses mob intent; existing player and object systems execute it.
    /// Each decision has one owner and may not be recomputed downstream:
    ///
    /// <list type="table">
    /// <item><term>Squad membership</term><description><see cref="SquadFormation"/> from live proximity,
    /// retained while committed to a move.</description></item>
    /// <item><term>Squad knowledge</term><description><see cref="SquadBlackboard"/>, whose contacts
    /// outlive individual perception.</description></item>
    /// <item><term>Which objective a squad is on</term><description><see cref="CommanderAi"/>.</description></item>
    /// <item><term>Movement, fire, and bearing</term><description><see cref="SquadTactics"/>, priced in
    /// <see cref="CombatValue"/>. Nothing else gates movement or fire.</description></item>
    /// <item><term>Shot value</term><description><see cref="WeaponEffectiveness"/>; a zero solution means
    /// hold fire.</description></item>
    /// <item><term>Where a man physically goes</term><description><see cref="NavigationSystem"/> and
    /// <see cref="PathFollower"/>, given a destination they do not choose.</description></item>
    /// <item><term>Replan persistence</term><description><see cref="Commitment{T}"/>.</description></item>
    /// </list>
    ///
    /// This class only orchestrates those owners and applies their output.
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
        /// <summary>
        /// Server-wide cover searches per tick. One starved cover selection without protecting the
        /// tick budget; use [ServerTick] p99 when tuning this limit.
        /// </summary>
        private const int CoverQueriesPerTick = 8;

        /// <summary>Vertical search around an explicit spawn point before falling back to the column surface.</summary>
        private const int SpawnColumnSearchCells = 6;
        private const float CoverThreatRequeryDistance = 5f;
        private const float TurnRadiansPerSecond = 180f * MathF.PI / 180f;
        private const float ObjectiveFormationRadius = 1.75f;
        private const float ObjectiveHoldRadius = FlagConfig.CaptureRadius - 0.35f;
        private static readonly float PartialTrapEscapeDistance =
            NavSearchOptions.Default.MinimumPartialDistance;
        private const float GoldenAngle = 2.39996323f;

        /// <summary>Far enough behind the digger to sample untouched ground rather than its own hole.</summary>
        /// <summary>Carriers close when their weapon's derived preferred range is below this threshold.</summary>
        private const float ClosesToFightRange = 25f;

        /// <summary>Minimum range for deliberate prone fire; matches CombatBehavior's precision-fire range.</summary>
        internal const float ProneMinimumEngagementRange = 30f;

        /// <summary>Minimum time standing after rising, preventing suppression-driven stance flicker.</summary>
        internal const int ProneReentryPenaltyTicks = 4 * NetworkConfig.TickRate;

        /// <summary>Fraction of health a blast must threaten before interrupting the current action.</summary>
        private const float GrenadeEvadeThreshold = 0.1f;

        private const float FoxholeGradeProbeDistance = 2.5f;
        private const uint ClearanceRecoveryTicks = 3 * NetworkConfig.TickRate;

        private const float IdleScanRadiansPerSecond = 45f * MathF.PI / 180f;

        /// <summary>Squad reform interval; avoids role churn and preserves in-progress bounds.</summary>
        private const uint SquadReformTicks = NetworkConfig.TickRate;
        private const uint TacticsReplanTicks = NetworkConfig.TickRate / 2;

        /// <summary>Arrival tolerance for a bound. Looser than cover, since the point is ground gained.</summary>
        private const float BoundArrivalDistance = 2.5f;

        /// <summary>A reload is readable at the same distance its sound curve carries the tell.</summary>
        internal const float EnemyReloadAwarenessRange = 100f;

        private readonly ChunkMap terrain;
        private readonly TerrainSystem terrainEdits;
        private readonly WeaponSystem weapons;
        private readonly ItemSystem items;
        private readonly CommanderAi commander;
        private readonly NavigationSystem navigation;
        private readonly Perception perception;
        private readonly GrenadeSystem grenades;
        private readonly MortarSystem? mortars;
        private readonly TeamIntelSystem? intel;

        /// <summary>Live grenades, rebuilt once per tick and shared by all actors.</summary>
        private List<LiveBlast> liveBlasts = [];

        private readonly CombatBehavior combat;
        private readonly GrenadeBehavior grenadeCombat;
        private readonly CoverBehavior cover;
        private readonly Random random;

        /// <summary>Kept beside the RNG rather than taken from it: see Marksmanship.SkillFactorFor.</summary>
        private readonly int mobSeed;
        private readonly Dictionary<ushort, Vector3> homes = new();
        private readonly Dictionary<ushort, MobBrain> brains = new();
        private readonly Dictionary<(int Team, int Squad), SquadBlackboard> squads = new();
        private readonly Queue<ushort> stuckMobs = new();
        private readonly Dictionary<int, List<SquadMember>> membersByTeam = new();
        private readonly Dictionary<ushort, int> squadAssignments = new();
        private readonly Dictionary<(int Team, int Squad), (List<ushort> Members, Vector3 Sum)>
            rosterScratch = new();
        private readonly List<(int Team, int Squad)> emptySquads = [];
        private readonly List<SquadMemberState> tacticalInputs = [];
        private readonly List<SquadTacticalOrder> tacticalOrders = [];

        // TEMPORARY navigation accounting for the conquest stuck-relocation investigation.
        internal sealed class NavDiag
        {
            public int Requests;
            public int Refused;
            public int Empty;
            public int Invalid;
            public int Trimmed;
            public int CoverMismatch;
            public int NotReplaceable;
            public int Installed;
            public int Superseded;
            public override string ToString()
                => $"req {Requests} refused {Refused} | empty {Empty} invalid {Invalid} "
                 + $"covermismatch {CoverMismatch} notreplaceable {NotReplaceable} "
                 + $"superseded {Superseded} trimmed {Trimmed} installed {Installed}";
        }

        private readonly Dictionary<ushort, NavDiag> navDiag = new();

        private NavDiag DiagFor(ushort actorId)
            => navDiag.TryGetValue(actorId, out var diag)
                ? diag
                : navDiag[actorId] = new NavDiag();

        /// <summary>Bounds begun since startup; exposes otherwise-unobservable manoeuvre activity.</summary>
        public int BoundsStarted { get; private set; }
        public int BoundsCompleted { get; private set; }

        /// <summary>Current squad membership and objectives, exposed for churn diagnostics.</summary>
        /// <summary>Personal contact memory; only <see cref="TeamIntelSystem"/> may aggregate it.</summary>
        internal ContactMemory? BeliefOf(ushort actorId)
            => brains.TryGetValue(actorId, out var brain) ? brain.Contacts : null;

        /// <summary>Path requests since startup; repeated requests reveal arrival/replan churn.</summary>
        internal long DebugPathRequests => navigation.SnapshotMetrics().Requested;

        internal NavigationSystem.Metrics DebugNavigationMetrics => navigation.SnapshotMetrics();

        internal IEnumerable<(ushort ActorId, int Team, int Squad, uint FlagId, Vector3 Destination)>
            DebugAssignments()
        {
            foreach (var pair in brains)
                yield return (
                    pair.Key,
                    pair.Value.Team,
                    pair.Value.SquadIndex,
                    squads.TryGetValue((pair.Value.Team, pair.Value.SquadIndex), out var board)
                        && board.TryGetObjective(out var objective)
                            ? objective.FlagId
                            : 0u,
                    pair.Value.Navigation.HasDestination
                        ? pair.Value.Navigation.Destination
                        : Vector3.Zero);
        }

        internal string DebugNavigation(ushort actorId)
        {
            if (!brains.TryGetValue(actorId, out var brain)) return "no brain";
            var route = brain.Navigation.Path.RemainingWaypoints.Take(4).ToArray();
            var board = BoardFor(brain);
            string objective = board.TryGetObjective(out var assigned)
                ? $"{assigned.FlagId}@{assigned.Position}"
                : "none";
            string resource = board.TryGetResourceObjective(actorId, out var assignedResource)
                ? $"{assignedResource.Kind}@{assignedResource.Position}"
                : "none";
            return $"intent {brain.DebugIntent}; squad {brain.SquadIndex}; objective {objective}; "
                 + $"resource {resource}; destination {brain.Navigation.Destination}; "
                 + $"pending {brain.Navigation.HasAnyPending}; path {brain.Navigation.Path.HasPath}; "
                 + $"partial attempts {brain.Navigation.PartialBacktrackAttempts}; "
                 + $"nav[{DiagFor(actorId)}]; "
                 + $"route [{string.Join(", ", route.Select(point => $"{point.Action}@{point.Position}"))}]";
        }

        // Temporary diagnostics for the hilltop-assault investigation. Actor-ticks, not events.
        public int DiagNoOrder;
        public int DiagRoleNone;
        public int DiagRoleBound;
        public int DiagRoleBaseOfFire;
        public int DiagBoundMoveCalls;
        public int DiagCoverMoveCalls;
        public int DiagMustEntrench;
        public int DiagPerceptionAttempts;
        public int DiagPerceptionObserved;
        public int DiagSquadHasThreat;
        public int DiagOwnContact;
        private int coverQueriesRemaining;
        private int timingTicks;
        private int timingAgentSamples;
        private long timingActorSamples;
        private long timingStationaryActorTicks;
        private long timingTerrainEdits;
        private long timingBoundsAttempted;
        private long timingBoundsCompleted;
        private double timingEngagementRangeMetres;
        private long timingEngagementSamples;
        private long timingMovementStopwatchTicks;

        /// <summary>
        /// The share of <see cref="timingMovementStopwatchTicks"/> that is the collision solver.
        ///
        /// The `movement` figure wraps the WHOLE per-mob tick — `GameWorld` times `MobSystem.Step`,
        /// which is brain, perception bookkeeping, path following, entrenchment and digging as well
        /// as `PlayerMovement.Step`. Reading it as "movement is expensive" sent one optimization pass
        /// at the voxel sampler on the strength of a number that never measured it. This splits the
        /// one part that is unambiguously the solver, so the remainder is attributable.
        /// </summary>
        private long timingSolverStopwatchTicks;

        /// <summary>
        /// Phases of the per-mob tick, so the `brain` remainder is attributable. It measured 3.5
        /// ms/tick climbing to 12 while the solver stayed flat at 1.2, and nothing said where it
        /// went. These wrap the two top-level decision branches of <see cref="Step"/>; whatever is
        /// left over after them and the solver is path following and bookkeeping.
        ///
        /// Inclusive of any <see cref="StepSolver"/> call made inside them — the solver total is
        /// reported separately and is the cross-cutting figure, not a fourth slice.
        /// </summary>
        private long timingCombatStopwatchTicks;
        private long timingEntrenchStopwatchTicks;

        /// <summary>
        /// Inside `follow`, which turned out to be ~100% of the AI cost once combat (4 us) and
        /// entrenchment (1 us) were measured and cleared. Three recovery probes run in an if/else-if
        /// chain ahead of the path follower, so each one is paid by every NPC that the previous one
        /// declined — and in the common case all three decline before the follower even runs.
        /// </summary>
        private long timingHeadroomStopwatchTicks;
        private long timingFollowerStopwatchTicks;
        private long timingPerceptionStopwatchTicks;
        private long timingCoverStopwatchTicks;
        private int timingCoverQueries;
        private int timingCoverRevalidations;
        private int timingCoverRevalidationsKept;
        private NavigationSystem.Metrics timingNavigationStart;
        private readonly List<long> timingNavigationQueueUs = [];
        private readonly List<long> timingNavigationSearchUs = [];
        private string latestStats = "AI stats are collecting their first 1-second window";

        public MobSystem(
            ChunkMap terrain,
            TerrainSystem terrainEdits,
            WeaponSystem weapons,
            ItemSystem items,
            FlagSystem flags,
            GrenadeSystem grenades,
            int seed = 0x51A7,
            MortarSystem? mortars = null,
            TeamIntelSystem? intel = null)
        {
            this.terrain = terrain;
            this.terrainEdits = terrainEdits;
            this.weapons = weapons;
            this.items = items;
            commander = new CommanderAi(flags, items.Objects);
            navigation = new NavigationSystem(terrain);
            perception = new Perception(terrain, weapons);
            this.grenades = grenades;
            this.mortars = mortars;
            this.intel = intel;
            combat = new CombatBehavior(weapons, terrain);
            grenadeCombat = new GrenadeBehavior(terrain, grenades);
            cover = new CoverBehavior(terrain);
            random = new Random(seed);
            mobSeed = seed;
        }

        public Vector3 RandomSpawnPoint() => RandomSurfacePoint(Vector3.Zero);

        /// <summary>Whether this actor currently leads its live squad roster.</summary>
        internal bool IsSquadLeader(ServerPlayer actor)
            => actor.IsMob
               && brains.TryGetValue(actor.Id, out var brain)
               && squads.TryGetValue((actor.Team, brain.SquadIndex), out var squad)
               && squad.LeaderId == actor.Id;

        /// <summary>
        /// Puts the NPC at the free space nearest the height it was ASKED for, and only falls back
        /// to the column's surface when the request names nowhere a body fits.
        ///
        /// This used to keep the caller's X and Z and re-derive the height through
        /// <see cref="PlayerMovement.SpawnAt"/>, which resolves the HIGHEST surface in the column —
        /// so a spawn carefully resolved to a building's floor was silently moved to that building's
        /// roof, and no amount of fixing the resolution upstream could show, because the answer was
        /// thrown away here. The fallback stays because callers legitimately ask for a column rather
        /// than a point: a scenario that spawns a mob at the origin means the ground at the origin,
        /// not a body twelve metres under it.
        /// </summary>
        public ServerPlayer CreateMob(ushort id, Vector3 position, int team = 1)
        {
            team = team > 0 ? team : 1;
            var mob = new ServerPlayer
            {
                Id = id,
                IsMob = true,
                Team = team,
                Move = NavTraversal.TryFindStandableNearestY(
                        terrain,
                        position.X,
                        position.Z,
                        position.Y,
                        SpawnColumnSearchCells,
                        out float standingY)
                    ? new MoveState
                    {
                        Position = position with { Y = standingY },
                        Velocity = Vector3.Zero,
                        Grounded = true,
                    }
                    : PlayerMovement.SpawnAt(terrain, position.X, position.Z),
            };
            homes[mob.Id] = mob.Position;
            brains[mob.Id] = CreateBrain(team, mob.Position, mob.Id);
            brains[mob.Id].Navigation.SetDestination(
                RandomSurfacePoint(mob.Position),
                clearPath: false);
            return mob;
        }

        public void BeginTick(uint tick, ICollection<ServerPlayer> actors)
        {
            PublishDebugStates(actors);
            liveBlasts = grenades.LiveBlasts(tick);
            coverQueriesRemaining = CoverQueriesPerTick;
            // Squads must exist before the commander assigns them anything, and the tactical plan reads
            // the roster the re-formation produced, so this ordering is load-bearing.
            ReformSquads(tick, actors);
            commander.Update(tick, squads, actors);
            ProcessGunshots(actors);
            ProcessIncomingFire(tick, actors);
            foreach (var pair in squads)
            {
                var squad = pair.Value;
                squad.Advance(tick);
            }
            PlanSquadTactics(tick, actors);
            while (navigation.TryGetCompleted(out var result))
            {
                if (!brains.TryGetValue(result.MobId, out var brain))
                    continue;
                if (!brain.Navigation.TryCompleteRequest(
                        result.RequestId,
                        out bool forCover))
                {
                    DiagFor(result.MobId).Superseded++;
                    continue;
                }
                if (forCover != brain.HasCoverDestination)
                {
                    DiagFor(result.MobId).CoverMismatch++;
                    continue;
                }

                if (result.Path.Waypoints.Count == 0)
                    DiagFor(result.MobId).Empty++;

                // Terrain moves between the worker stamping this route and the main thread reaching
                // it, so the arriving path is trimmed to what is still executable rather than
                // rejected whole. Rejecting it leaves the actor with no path at all, and an actor
                // with no path digs — which invalidates the next actor's route in the same place.
                if (result.Path.Waypoints.Count > 0
                    && NavPathTerrain.TryTrimToValid(terrain, result.Path, out var usable))
                {
                    if (usable.Waypoints.Count < result.Path.Waypoints.Count)
                        DiagFor(result.MobId).Trimmed++;
                    if (brain.Navigation.Path.CanReplacePath)
                        DiagFor(result.MobId).Installed++;
                    else
                        DiagFor(result.MobId).NotReplaceable++;
                    // A jump path's intent was proved as one continuous movement. A prefetch can
                    // finish after takeoff; installing it then resets the jump executor while the
                    // actor is airborne and is enough to drop a capsule off a narrow bridge.
                    // Discard that stale prefix and let the landed actor request from truth.
                    if (brain.Navigation.Path.CanReplacePath)
                    {
                        Vector3? currentPosition = actors
                            .FirstOrDefault(actor => actor.Id == result.MobId)
                            ?.Position;
                        brain.Navigation.Path.SetPath(
                            usable,
                            result.TerrainVersion,
                            currentPosition,
                            terrain);
                    }
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
                                ? ObjectiveDestination(result.MobId, objective.Position, squad)
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
                    DiagPerceptionAttempts++;
                    if (perception.Tick(actor, actors, brain, tick) is { } observed)
                    {
                        DiagPerceptionObserved++;
                        squad.Publish(observed, tick);
                    }
                    if (squad.TryGetPrimaryThreat(tick, out _)) DiagSquadHasThreat++;
                    if (brain.Contacts.TryNearest(actor.Position, tick, out _)) DiagOwnContact++;
                }
            timingPerceptionStopwatchTicks += Stopwatch.GetTimestamp() - started;
        }

        public void Step(
            ServerPlayer mob,
            float dt,
            uint tick,
            ICollection<ServerPlayer> actors)
        {
            int boundIndexBefore = brains.TryGetValue(mob.Id, out var existingBrain)
                ? existingBrain.BoundIndex
                : 0;
            long terrainVersionBeforeDecision = terrain.EditVersion;
            var action = Decide(mob, dt, tick, actors) with
            {
                // Measure the authoritative edit, not whether a decision branch wanted to swing.
                // Cooldowns and already-empty bites legitimately produce no terrain change, while
                // combat/cover helpers do not otherwise have a clean way to bubble the edit back.
                TerrainProgress = terrain.EditVersion != terrainVersionBeforeDecision,
            };
            timingActorSamples++;
            if (action.Intent.LengthSquared() < 0.01f)
                timingStationaryActorTicks++;
            if (action.TerrainProgress)
                timingTerrainEdits++;
            if (brains.TryGetValue(mob.Id, out var sampledBrain))
            {
                if (sampledBrain.BoundIndex > boundIndexBefore)
                {
                    BoundsCompleted += sampledBrain.BoundIndex - boundIndexBefore;
                    timingBoundsCompleted += sampledBrain.BoundIndex - boundIndexBefore;
                }
                if (sampledBrain.CombatTargetId != 0
                    && sampledBrain.Contacts.TryGet(
                        sampledBrain.CombatTargetId,
                        tick,
                        out var engagement))
                {
                    timingEngagementRangeMetres += HorizontalDistance(
                        mob.Position,
                        engagement.Position);
                    timingEngagementSamples++;
                }
            }
            // Charge the stance transition regardless of which decision arm made the actor stand.
            // Keeping this inside the combat arm let a one-tick contact loss bypass the penalty.
            if (mob.State.HasFlag(PlayerStateFlags.Prone)
                && !action.Prone
                && brains.TryGetValue(mob.Id, out var stanceBrain))
                stanceBrain.NextProneTick = tick + ProneReentryPenaltyTicks;
            Apply(mob, action, dt);

            // After the move, because it measures whether the move achieved anything.
            if (brains.TryGetValue(mob.Id, out var brain)
                && brain.Navigation.Progress.Update(
                    mob.Position,
                    tick,
                    expectedToTravel: !action.HoldingObjective,
                    action.TerrainProgress))
                stuckMobs.Enqueue(mob.Id);
        }

        /// <summary>
        /// What this actor does this tick, as a value. Reads the brain and the blackboard; writes
        /// NEITHER the actor's State/Yaw/Pitch/LastIntent NOR its position. <see cref="Apply"/> does
        /// that, once, which is the whole point — a decider with no write access cannot overwrite
        /// another decider's answer.
        ///
        /// It still mutates the BRAIN — navigation destinations, cover claims, contact memory — and
        /// that is correct: the brain is the blackboard, and deciding is what updates it.
        /// </summary>
        private MobAction Decide(
            ServerPlayer mob,
            float dt,
            uint tick,
            ICollection<ServerPlayer> actors)
        {
            if (!brains.TryGetValue(mob.Id, out var brain))
                brains[mob.Id] = brain = CreateBrain(mob.Team, mob.Position, mob.Id);
            var squad = BoardFor(mob, brain);

            // Before everything, including the squad's manoeuvre. Nothing this man was doing is worth
            // standing in a blast for, and the squad would rather have him than the ground.
            if (liveBlasts.Count > 0
                && GrenadeDanger.Evaluate(mob.Position, liveBlasts, out var away)
                    >= GrenadeEvadeThreshold)
            {
                brain.DebugIntent = new ActorIntent.EvadeBlast(away).DebugLabel;
                brain.Navigation.Progress.Reset();
                return new MobAction
                {
                    Intent = away,
                    Sprint = true,
                    Yaw = MathF.Atan2(away.X, away.Z),
                    TurnTo = true,
                };
            }

            bool hasResource = squad.TryGetResourceObjective(mob.Id, out var resource);
            ServerObject? resourceObject = null;
            if (hasResource && !items.Objects.TryGet(resource.ObjectId, out resourceObject))
            {
                squad.SetResourceObjective(null);
                hasResource = false;
            }

            if (hasResource
                && HorizontalDistanceSquared(mob.Position, resource.Position)
                    <= PickupTargeting.RadiusSquared)
            {
                if (resource.Kind == SquadResourceKind.AcquireWeapon)
                {
                    if (items.TryTake(mob, resourceObject!, actors))
                    {
                        squad.SetResourceObjective(null);
                        brain.DebugIntent = "EQUIP";
                        brain.Navigation.Clear();
                        return new MobAction { HoldingObjective = true };
                    }
                }
                else if (resource.Kind == SquadResourceKind.OperateMortar
                         && mortars is not null
                         && resourceObject!.Has.HasFlag(NetComponents.Item | NetComponents.Transform)
                         && ItemCatalog.HasBehavior(resourceObject.Item.Type, ItemBehavior.Mortar)
                         && CommanderAi.MortarTargetIsSafe(mob.Team, resource.Target, actors)
                         && (mob.OperatingObjectId == resourceObject.NetworkId
                             || !ItemSystem.IsBeingWorked(resourceObject.NetworkId, actors)))
                {
                    mob.OperatingObjectId = resourceObject.NetworkId;
                    _ = mortars.TryFire(mob, resourceObject, resource.Target, tick);
                    brain.DebugIntent = "MORTAR";
                    brain.Navigation.Progress.Reset();
                    return new MobAction
                    {
                        Yaw = MathF.Atan2(
                            resource.Target.X - mob.Position.X,
                            resource.Target.Z - mob.Position.Z),
                        TurnTo = true,
                        HoldingObjective = true,
                    };
                }
            }

            if (!hasResource && mob.IsOperating)
                mob.OperatingObjectId = 0;

            bool hasFlagObjective = squad.TryGetObjective(out var flagObjective);
            bool hasObjective = hasResource || hasFlagObjective;
            var objective = hasResource
                ? new SquadObjective(resource.ObjectId, resource.Position)
                : flagObjective;
            Vector3 AssignedObjectiveDestination()
                => hasResource
                    ? objective.Position
                    : ObjectiveDestination(mob.Id, objective.Position, squad);

            if (hasResource && brain.ResourceRevision != squad.ResourceRevision)
            {
                brain.ResourceRevision = squad.ResourceRevision;
                brain.ObjectiveReached = false;
                brain.Navigation.Path.Clear();
                brain.Navigation.ResetBlocked();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(objective.Position);
            }
            else if (!hasResource && brain.ObjectiveRevision != squad.ObjectiveRevision)
            {
                brain.ObjectiveRevision = squad.ObjectiveRevision;
                brain.ObjectiveReached = false;
                brain.Navigation.Path.Clear();
                brain.Navigation.ResetBlocked();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    hasObjective
                        ? AssignedObjectiveDestination()
                        : RandomSurfacePoint(HomeOf(mob)));
            }
            if (!brain.Navigation.HasDestination)
                brain.Navigation.SetDestination(
                    hasObjective
                        ? AssignedObjectiveDestination()
                        : RandomSurfacePoint(HomeOf(mob)));
            Vector3 destination = brain.Navigation.Destination;

            // Hearing is free and turning to look is correct; WALKING to the sound is the part that
            // has to be worth it. Same gate as incoming fire, for the same reason — a firefight two
            // hundred metres away is information, not an order to abandon an objective.
            bool hasRememberedShot = brain.TryRecentGunshot(tick, out var heardShot);
            bool heardGunshot = hasRememberedShot
                && ThreatResponse.IsWorthAnswering(
                    SelfCombatant(mob, brain),
                    IncomingFrom(mob, brain, heardShot.Position),
                    StrategicValue.TicketsPerSecondPerFlag);
            if (heardGunshot
                && !brain.IsAppliedGunshot(heardShot))
            {
                brain.ApplyGunshot(heardShot);
                brain.ObjectiveReached = false;
                brain.Navigation.Path.Clear();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    SurfaceQuery.SurfacePosition(
                        terrain,
                        heardShot.Position.X,
                        heardShot.Position.Z));
                destination = brain.Navigation.Destination;
            }
            else if (!heardGunshot && brain.HasAppliedGunshot)
            {
                brain.ClearGunshots();
                brain.ObjectiveReached = false;
                brain.Navigation.Path.Clear();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    hasObjective
                        ? AssignedObjectiveDestination()
                        : RandomSurfacePoint(HomeOf(mob)));
                destination = brain.Navigation.Destination;
            }

            if (grenadeCombat.TryThrow(mob, brain, squad, actors, tick))
            {
                brain.DebugIntent = "GRENADE";
                brain.Navigation.Progress.Reset();
                return new MobAction { Shooting = true };
            }
            mob.Hotbar = HotbarSlot.Primary;
            // The clear that used to be here is gone: Shooting means actuating the CURRENT item, and
            // a shovel swing must not carry through the hotbar transition and look like the newly
            // equipped primary firing. Apply now composes mob.State from the action alone, so no flag
            // survives a tick unless this tick's decision asked for it — the guarantee that clear was
            // approximating.

            bool hasOrder = squad.TryGetOrder(mob.Id, out var order);
            bool bounding = hasOrder && order.Role == SquadRole.Bound;
            if (!hasOrder) DiagNoOrder++;
            else if (order.Role == SquadRole.None) DiagRoleNone++;
            else if (order.Role == SquadRole.Bound) DiagRoleBound++;
            else DiagRoleBaseOfFire++;
            // What his weapon wants, not what his weapon IS. This was
            // `activePrimary.Item.Type == NpcSquadLoadout.AssaultWeapon` — the last weapon-identity
            // branch in the AI, one indirection deeper than the ItemType.Ppsh tests that were
            // removed, and the reason a submachine gunner was treated as a special case rather than
            // as a man with a short weapon. Derived from the damage curve, a new close-range weapon
            // inherits the behaviour without anybody naming it here.
            bool closesToFight = weapons.TryGetActiveWeapon(mob, out var activePrimary)
                && WeaponEffectiveness.PreferredRange(activePrimary.Item.Type, brain.SkillFactor)
                    <= ClosesToFightRange;
            // A base of fire holds and shoots at where the target is, not only at a target it can
            // currently see. That is what buys the bounding man his move.
            // Being ordered to the base of fire IS being the base of fire. It used to also require
            // brain.IsSet (i.e. AtCover), which meant a man who could not reach cover never
            // suppressed, so nobody was ever covered, so nobody ever moved.
            bool suppressing = hasOrder && order.Role == SquadRole.BaseOfFire;
            // "Is this shot worth taking from here?" is now answered by the firing solution rather
            // than by asking whether the weapon happens to be a PPSh. A weapon whose expected return
            // per round falls below WeaponEffectiveness.MinimumExpectedDamagePerRound yields a zero
            // solution, which IS the decision to hold fire and close instead.
            bool hasNearestContact =
                brain.Contacts.TryNearest(mob.Position, tick, out var nearestContact);
            float engagementRange = hasNearestContact
                ? HorizontalDistance(mob.Position, nearestContact.Position)
                : 0f;
            bool holdingForEffectiveRange =
                hasNearestContact
                && weapons.TryGetPrimaryWeapon(mob, out var rangeWeapon)
                && WeaponEffectiveness.Best(
                    rangeWeapon.Item.Type,
                    engagementRange,
                    // Reach, not this target's cover — see the matching note in CombatBehavior.
                    TargetExposure.Full,
                    extraMoa: 0f,
                    brain.SkillFactor).DamagePerSecond <= 0f;

            ActorIntent decision = bounding
                ? new ActorIntent.Bound(order.Destination, order.Bearing)
                : hasNearestContact
                    ? ScoreCombatIntent(
                        mob,
                        brain,
                        nearestContact,
                        mayEntrench: hasOrder
                            && order.Role == SquadRole.BaseOfFire
                            && !brain.HasCompletedInitialEntrenchment
                            && !brain.Entrenched)
                    : brain.Entrenching || brain.Entrenched
                        ? new ActorIntent.HoldFightingPosition()
                        : new ActorIntent.PursueObjective();

            bool readyAtEntrenchPeek = !brain.Entrenched
                || (!ShouldCrouchAtCover(brain, tick)
                    && HorizontalDistanceSquared(mob.Position, brain.CoverPeekPosition)
                        <= CoverArrivalDistance * CoverArrivalDistance);
            // No engagement permit. Being assigned to the base of fire IS permission to shoot —
            // the blackboard used to hand out two rotating three-second firing turns per squad, so
            // four of six men were forbidden to fire at any moment while the allocation had them
            // down as the base of fire.
            bool mayFire = decision is ActorIntent.HoldAndFire
                && !holdingForEffectiveRange
                && readyAtEntrenchPeek;
            bool underFire =
                brain.IsUnderFire(tick)
                || mob.Spread.SuppressionMoa > 1f;
            long combatStarted = Stopwatch.GetTimestamp();
            var combatOutcome = combat.Tick(
                mob,
                brain,
                tick,
                dt,
                mayFire && !underFire,
                suppressing && mayFire);
            bool combatOwnsTick = combatOutcome.OwnsTick;
            timingCombatStopwatchTicks += Stopwatch.GetTimestamp() - combatStarted;

            // A bound is executed whether or not COMBAT owns the tick.
            //
            // This branch used to sit entirely inside `if (combatOwnsTick)`, and combat.Tick returns
            // false whenever this actor personally has no contact — which is 36% of the time, because
            // perception's 110 degree field of view means a man running a flank cannot see the enemy
            // he is flanking. The squad believed in the threat 94% of the time and ordered the bound;
            // the individual didn't, so his own state vetoed it and he wandered off to his objective
            // instead. Measured, 1742 actor-ticks under a bound order produced 892 bound movements.
            //
            // Manoeuvre is a SQUAD decision. Requiring the mover to independently agree there is an
            // enemy is the same two-authorities mistake as the engagement permits.
            // ONE decision, made here and nowhere else. Everything below reads `intent`; nothing
            // recomputes whether this man may move or shoot. See ActorIntent for why.
            // Order matters and is load-bearing:
            //
            //   Bound first, because a squad manoeuvre outranks this actor's own combat state — a
            //     flanker cannot see the man he is flanking, so requiring his personal agreement was
            //     what stopped half the bounds executing.
            //   Holding, entrenching and seeking cover are compared in net HP/s once combat is live.
            //   HoldFightingPosition and PursueObjective last, both outside combat. A man in or
            //     building a hole works it; only a man with neither goes back to his objective.
            //     Getting entrenchment ABOVE PursueObjective is what stopped every NPC digging at
            //     spawn instead of advancing, so the two must stay below the combat arms.
            //
            // HoldFightingPosition used to not exist: the two branches below this decision tested
            // brain.Entrenching/Entrenched directly and moved an actor that had already been told to
            // pursue its objective. That is what made the claim above ("nothing recomputes whether
            // this man may move") false, and made the states overlay draw OBJECTIVE over a man in a
            // foxhole.
            // Every path below this point is downstream of the one decision, so labelling it here
            // covers all of them and cannot drift from what the actor actually did.
            brain.DebugIntent = decision.DebugLabel;

            // Named positively. It used to read `is not PursueObjective`, which quietly meant "every
            // intent that exists except one" — so adding HoldFightingPosition put a man in a foxhole
            // down the combat movement path. A closed union earns nothing if its consumers match on
            // the complement of one case.
            if (decision is ActorIntent.Bound or ActorIntent.Entrench or ActorIntent.SeekCover)
            {
                brain.Navigation.Progress.Reset();
                Vector3 combatIntent;
                bool combatJump;
                bool combatDigging;
                if (decision is ActorIntent.Entrench) DiagMustEntrench++;
                if (decision is ActorIntent.Bound)
                {
                    DiagBoundMoveCalls++;
                    UpdateBoundMovement(
                        mob,
                        brain,
                        squad,
                        order,
                        closesToFight,
                        tick,
                        out combatIntent,
                        out combatJump,
                        out combatDigging);
                }
                else
                {
                    bool wantsAdvance = brain.ShouldCloseDistance || !mayFire || underFire;
                    // No advance permit either. Whether this man moves is the allocation's call,
                    // and it already made it — Bound moves, BaseOfFire holds. A second rotating
                    // lease could only disagree with it.
                    // No weapon-keyed veto. This used to be `!assaultWaitingInPosition && ...`,
                    // which forbade a submachine gunner assigned to the base of fire from advancing
                    // — parking him at a range where his weapon does nothing, which is the reported
                    // "assault units are useless at long range". Whether a man moves is the
                    // allocation's call and SquadTactics already makes it by score; a second veto
                    // keyed on weapon type is the two-authorities mistake ActorIntent exists to
                    // prevent.
                    bool mayAdvance = underFire || wantsAdvance;
                    DiagCoverMoveCalls++;
                    UpdateCoverMovement(
                        mob,
                        brain,
                        squad,
                        mayAdvance,
                        brain.ShouldCloseDistance,
                        underFire,
                        hasOrder && order.Role == SquadRole.BaseOfFire,
                        decision is ActorIntent.Entrench,
                        closesToFight,
                        tick,
                        out combatIntent,
                        out combatJump,
                        out combatDigging);
                }
                // From the OUTCOME, not from mob.State. Reading the actor here would be reading
                // last tick's answer, because nothing has applied this tick's yet.
                bool crouching = brain.AtCover
                    && !bounding
                    && (combatOutcome.Flags.HasFlag(PlayerStateFlags.Reloading)
                        || ShouldCrouchAtCover(brain, tick));

                // Run when the movement IS the job and shooting is not: bounding across open ground,
                // closing on cover not yet reached, or holding a weapon the squad has not cleared
                // you to use. Never while firing or tucked in — SprintingMoa and the post-sprint
                // penalty mean a man who sprints and shoots does neither well, so the state flags
                // that buy the speed also pay for it.
                // A bound sprints unconditionally. Crossing open ground is the whole job, and the
                // squad is putting fire on the threat precisely so this man does not have to — the
                // suppression term in SquadTactics is what paid for the move in the first place.
                //
                // The `!Shooting` guard below deliberately does not apply to him. It used to, and
                // once bounds began executing outside the combat gate a mover had usually already
                // been through combat.Tick and picked up Shooting, so the flag that makes him fast
                // was cancelled by the flag that makes him inaccurate — he walked, and shot badly.
                bool sprinting = combatIntent != Vector3.Zero
                    && !crouching
                    && (decision is ActorIntent.Bound
                        || !combatOutcome.Flags.HasFlag(PlayerStateFlags.Shooting)
                            && (!brain.AtCover || !mayFire));
                bool prone = ShouldGoProne(
                    combatIntent,
                    underFire,
                    brain.AtCover,
                    combatDigging,
                    combatOwnsTick,
                    engagementRange,
                    tick,
                    brain.NextProneTick);
                // Combat's own flags carried explicitly rather than by merging onto whatever
                // mob.State happened to hold, which is how a stale flag used to survive a tick.
                return new MobAction
                {
                    Intent = combatIntent,
                    Jump = combatJump,
                    Sprint = sprinting,
                    Crouch = crouching && !prone,
                    Prone = prone,
                    // Either kind of actuation: a trigger pull or a shovel bite taken on the way.
                    Shooting = combatOutcome.Flags.HasFlag(PlayerStateFlags.Shooting) || combatDigging,
                    Aiming = combatOutcome.Flags.HasFlag(PlayerStateFlags.Aiming),
                    Reloading = combatOutcome.Flags.HasFlag(PlayerStateFlags.Reloading),
                    // A man swinging a shovel looks where he is digging, not where he was aiming.
                    // The dig helper set that facing during Decide and it used to win by running
                    // last; ordering is not a mechanism any more, so the preference is stated.
                    Yaw = combatDigging ? mob.Yaw : combatOutcome.Yaw,
                    Pitch = combatDigging ? mob.Pitch : combatOutcome.Pitch,
                    TurnTo = combatOwnsTick || combatDigging,
                };
            }

            if (decision is ActorIntent.HoldAndFire)
            {
                Vector3 holdIntent = Vector3.Zero;
                bool holdJump = false;
                bool tucked = false;
                if (brain.Entrenched)
                {
                    tucked = ShouldCrouchAtCover(brain, tick);
                    Vector3 desired = tucked
                        ? brain.CoverDestination
                        : brain.CoverPeekPosition;
                    Vector3 delta = desired - mob.Position;
                    if (HorizontalDistanceSquared(desired, mob.Position)
                        > CornerArrivalDistance * CornerArrivalDistance)
                    {
                        delta.Y = 0f;
                        if (delta.LengthSquared() > 1e-6f)
                            holdIntent = Vector3.Normalize(delta);
                    }
                    holdJump = !tucked
                        && desired.Y > mob.Position.Y + 0.2f
                        && mob.Move.Grounded;
                }
                // A finished foxhole is designed around a crouched protected station and a
                // standing peek. Going prone here leaves the capsule below the planned stations,
                // so it never reaches the peek and never fires despite having completed the cut.
                bool prone = !brain.Entrenched && ShouldGoProne(
                    holdIntent,
                    underFire,
                    brain.AtCover,
                    digging: false,
                    combatOwnsTick,
                    engagementRange,
                    tick,
                    brain.NextProneTick);
                return new MobAction
                {
                    Intent = holdIntent,
                    Jump = holdJump,
                    Crouch = !prone && brain.AtCover
                        && (brain.Entrenched ? tucked : ShouldCrouchAtCover(brain, tick)),
                    Prone = prone,
                    Shooting = combatOutcome.Flags.HasFlag(PlayerStateFlags.Shooting),
                    Aiming = combatOutcome.Flags.HasFlag(PlayerStateFlags.Aiming),
                    Reloading = combatOutcome.Flags.HasFlag(PlayerStateFlags.Reloading),
                    Yaw = combatOutcome.Yaw,
                    Pitch = combatOutcome.Pitch,
                    TurnTo = combatOwnsTick,
                };
            }

            // Digging a fighting position outlasts direct sight: once the actor drops below grade,
            // the parapet itself hides the target for several seconds. Abandoning on contact expiry
            // made a half-dug hole and sent the NPC roaming. Finish the fixed plan, then cycle from
            // its protected centre to the peek station so perception can reacquire naturally.
            long entrenchStarted = Stopwatch.GetTimestamp();
            if (decision is ActorIntent.HoldFightingPosition && brain.Entrenching)
            {
                var rememberedThreat = new AiContact(
                    brain.CoverThreatId,
                    brain.CoverThreatPosition,
                    tick,
                    1f);
                _ = UpdateEntrenchment(
                    mob,
                    brain,
                    squad,
                    rememberedThreat,
                    tick,
                    out var entrenchIntent,
                    out bool entrenchJump,
                    out bool entrenchDigging);
                bool entrenchCrouch = brain.Entrenched
                    && ShouldCrouchAtCover(brain, tick);
                // Accumulated here as well as at the end of the block: the early return used to skip
                // it, so every tick actually spent entrenching was missing from `ai stats`.
                timingEntrenchStopwatchTicks += Stopwatch.GetTimestamp() - entrenchStarted;
                return new MobAction
                {
                    Intent = entrenchIntent,
                    Jump = entrenchJump,
                    Crouch = entrenchCrouch,
                    Shooting = entrenchDigging,
                    TerrainProgress = true,
                };
            }
            if (decision is ActorIntent.HoldFightingPosition && brain.Entrenched)
            {
                bool tucked = ShouldCrouchAtCover(brain, tick);
                Vector3 desired = tucked
                    ? brain.CoverDestination
                    : brain.CoverPeekPosition;
                Vector3 delta = desired - mob.Position;
                bool move = HorizontalDistanceSquared(desired, mob.Position)
                    > CornerArrivalDistance * CornerArrivalDistance;
                Vector3 coverIntent = Vector3.Zero;
                if (move)
                {
                    delta.Y = 0f;
                    if (delta.LengthSquared() > 1e-6f)
                        coverIntent = Vector3.Normalize(delta);
                }
                bool coverJump = !tucked
                    && desired.Y > mob.Position.Y + 0.2f
                    && mob.Move.Grounded;
                if (!tucked
                    && HorizontalDistanceSquared(mob.Position, brain.CoverPeekPosition)
                        <= CoverArrivalDistance * CoverArrivalDistance)
                    brain.Contacts.Observe(
                        brain.CoverThreatId,
                        brain.CoverThreatPosition,
                        tick);
                timingEntrenchStopwatchTicks += Stopwatch.GetTimestamp() - entrenchStarted;
                return new MobAction
                {
                    Intent = coverIntent,
                    Jump = coverJump,
                    Crouch = tucked,
                };
            }
            timingEntrenchStopwatchTicks += Stopwatch.GetTimestamp() - entrenchStarted;

            if (brain.HasCoverDestination)
            {
                ClearCover(mob.Id, brain, squad);
                CancelPending(mob.Id, brain.Navigation, forCover: true);
            }
            var follower = brain.Navigation.Path;
            // Arrived where HE was sent, not where the squad's objective is.
            //
            // This was measured against objective.Position — the flag — while the destination every
            // man but the point walks to is his slot in the wedge, ten to twenty metres off it. So a
            // flanker reached his slot, ObjectiveReached went true, and this test said no: he was not
            // holding, so the follower was not cleared, so the next tick found no path and asked for
            // one, arrived on it in a metre, and asked again. Measured on the conquest map, a hundred
            // path requests a second across thirty-two actors, twelve nodes each, and 5.7 direction
            // reversals per five seconds in the windows where men covered ground without getting
            // anywhere. That is the reported rubber-banding, and the destination never changed once
            // while it was happening — the churn was between the follower and the search, not in any
            // assignment.
            //
            // The radius keeps its old value and gains a second meaning it already fits: for the
            // point man it is still "inside the capture radius", and for everyone else it is "on my
            // station".
            bool holdingObjective =
                hasObjective
                && brain.ObjectiveReached
                && !heardGunshot
                && HorizontalDistanceSquared(mob.Position, destination)
                    <= ObjectiveHoldRadius * ObjectiveHoldRadius;
            PathFollowState followState;
            Vector3 intent;
            bool jump;
            bool digging = false;
            bool terrainProgress = false;
            bool replacePendingPath = false;
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
                if (TimedClearNavigationHeadroom(
                        mob,
                        brain,
                        destination,
                        tick,
                        out intent,
                        out jump,
                        out digging,
                        out terrainProgress))
                {
                    followState = PathFollowState.Following;
                }
                else
                {
                    long followerStarted = Stopwatch.GetTimestamp();
                    followState = follower.Update(
                        mob.Position,
                        mob.Move.Grounded,
                        terrain.EditVersion,
                        // The same wedge the destination uses, applied as a lane during the march
                        // rather than only as a place to end up — which is why a squad crossing open
                        // ground used to arrive in formation having been a single file the whole way.
                        // Formation is an open-ground preference, not recovery geometry. Once a
                        // bounded prefix has failed to carry the actor out, use the proved route's
                        // centre line; continuing to add the wedge lane can hold an outer member
                        // against the same spawn wall while the rest of its squad departs.
                        brain.Navigation.HasBegunPartialRecovery
                            ? Vector3.Zero
                            : WedgeLateralOffset(mob, brain, squad),
                        out intent,
                        out jump,
                        out var digTarget,
                        out var blockedCell);
                    timingFollowerStopwatchTicks += Stopwatch.GetTimestamp() - followerStarted;
                    brain.Navigation.RememberBlocked(blockedCell);
                    replacePendingPath = blockedCell is not null;
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
                            tick,
                            plannedExcavation: true,
                            narrowCorridor: brain.Navigation.PreciseExcavation);
                        terrainProgress = terrain.EditVersion != versionBeforeDig;
                        if (terrainProgress)
                        {
                            brain.Navigation.RememberDigSite(digTarget, tick);
                            squad.LeaseExcavation(mob.Id, digTarget, tick);
                            // The edit invalidates the executor's old collision evidence. Let the
                            // next authoritative search reconsider the changed edge from scratch.
                            brain.Navigation.ResetBlocked();
                        }
                    }
                    if (followState == PathFollowState.Complete)
                    {
                        bool reachedGoal = follower.ReachedGoal;
                        NavCell? partialStart = follower.PartialStartCell;
                        follower.Clear();
                        if (reachedGoal && heardGunshot)
                        {
                            brain.ForgetGunshot(heardShot.ShooterId);
                            heardGunshot = false;
                            brain.Navigation.SetDestination(
                                hasObjective
                                    ? AssignedObjectiveDestination()
                                    : RandomSurfacePoint(HomeOf(mob)));
                            destination = brain.Navigation.Destination;
                            followState = PathFollowState.NeedsPath;
                        }
                        else if (reachedGoal && hasObjective)
                        {
                            brain.Navigation.ClearPartialBacktrack();
                            brain.ObjectiveReached = true;
                            followState = PathFollowState.Following;
                        }
                        else
                        {
                            // A prefetch was queued from an older point on this partial corridor.
                            // Once the actor consumes the prefix, replace that stale request from
                            // the actual frontier so excavation and newly visible detours compete.
                            replacePendingPath = !reachedGoal;
                            if (!reachedGoal)
                            {
                                if (HasLeftPartialTrap(mob.Position, destination, partialStart))
                                    brain.Navigation.ClearPartialBacktrack();
                                else
                                    brain.Navigation.RememberPartialBacktrack(partialStart);
                            }
                            if (reachedGoal)
                            {
                                brain.ObjectiveReached = false;
                                brain.Navigation.SetDestination(
                                    RandomSurfacePoint(HomeOf(mob)));
                                destination = brain.Navigation.Destination;
                            }
                            followState = PathFollowState.NeedsPath;
                        }
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
                    tick,
                    forCover: false,
                    replacePending: replacePendingPath,
                    allowDig: true,
                    priority: NavigationPriority.MissingPath);
                if (!requested && heardGunshot)
                {
                    brain.ForgetGunshot(heardShot.ShooterId);
                    heardGunshot = false;
                    brain.Navigation.SetDestination(
                        hasObjective
                            ? AssignedObjectiveDestination()
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
                // while the background worker pool plans, avoiding walk/wait/walk oscillation.
                RequestPath(
                    mob,
                    destination,
                    tick,
                    forCover: false,
                    allowDig: true,
                    priority: NavigationPriority.Prefetch);
            }

            // The follower turns toward where it is going, a digging man keeps the heading and pitch
            // his shovel needs, and an idle man on his objective scans. Three `mob.Yaw = ...` writes
            // at this point, now one answer.
            float followYaw = mob.Yaw;
            float followPitch = digging ? mob.Pitch : 0f;
            if (intent != Vector3.Zero)
                followYaw = RotateYawTowards(
                    mob.Yaw,
                    MathF.Atan2(intent.X, intent.Z),
                    TurnRadiansPerSecond * dt);
            else if (!digging && heardGunshot)
                followYaw = YawTowardHorizontal(mob, heardShot.Position, dt);
            else if (!digging && holdingObjective)
                followYaw = NormalizeRadians(mob.Yaw + IdleScanRadiansPerSecond * dt);

            return new MobAction
            {
                Intent = intent,
                Jump = jump,
                // Sprinting is NOT here. It belongs to the combat path, where there is something to
                // run from or toward; a squad that runs everywhere reads as panicked rather than
                // urgent, and arrives with PostSprintMoa still spoiling its first three seconds of
                // fire. Shooting reads as "actuating the held item", which is what the client's view
                // of a shovel swings on — digging is the tool's version of pulling the trigger.
                Shooting = digging,
                Yaw = followYaw,
                Pitch = followPitch,
                TurnTo = true,
                HoldingObjective = holdingObjective,
                TerrainProgress = terrainProgress,
            };
        }

        private bool TryClearNavigationHeadroom(
            ServerPlayer mob,
            MobBrain brain,
            Vector3 destination,
            uint tick,
            out Vector3 intent,
            out bool jump,
            out bool digging,
            out bool terrainProgress)
        {
            intent = Vector3.Zero;
            jump = false;
            digging = false;
            terrainProgress = false;
            bool hasActorCell = TryActorCell(mob.Position, out _);
            if (!hasActorCell
                && (brain.Navigation.Path.HasPath || brain.Navigation.HasAnyPending))
                return false;
            if (hasActorCell
                && !brain.Navigation.Progress.HasStalledFor(
                    mob.Position,
                    tick,
                    ClearanceRecoveryTicks))
                return false;

            Vector3 toward = destination - mob.Position;
            int dx;
            int dz;
            if (MathF.Abs(toward.X) > MathF.Abs(toward.Z))
            {
                dx = toward.X < 0f ? -1 : 1;
                dz = 0;
            }
            else
            {
                dx = 0;
                dz = toward.Z < 0f ? -1 : 1;
            }
            bool found = NavTraversal.TryDigClearance(
                terrain,
                mob.Position,
                dx,
                dz,
                out var target);
            if (!found)
            {
                Vector3 nextFeet = mob.Position + new Vector3(dx, 0f, dz);
                found = NavTraversal.TryDigClearance(
                        terrain,
                        nextFeet,
                        dx,
                        dz,
                        out target)
                    && Digging.InReach(mob.Position, target);
            }
            if (!found
                && toward.Y > MathF.Sqrt(toward.X * toward.X + toward.Z * toward.Z))
                foreach (var (verticalDx, verticalDz) in (ReadOnlySpan<(int X, int Z)>)[
                             (0, 1), (1, 0), (0, -1), (-1, 0)])
                {
                    Vector3 nextFeet = mob.Position + new Vector3(verticalDx, 0f, verticalDz);
                    if (!NavTraversal.TryDigClearance(
                            terrain,
                            nextFeet,
                            verticalDx,
                            verticalDz,
                            out target)
                        || !Digging.InReach(mob.Position, target))
                        continue;
                    found = true;
                    break;
                }
            if (!found) return false;

            digging = true;
            long version = terrain.EditVersion;
            PerformNavigationDig(mob, brain, target, tick);
            terrainProgress = terrain.EditVersion != version;
            return true;
        }

        /// <summary>
        /// Wipes the fight out of a brain when its NPC comes back on a respawn wave. Respawn reset the
        /// replicated ServerPlayer but never the brain, so a mob returned to base still believing it was
        /// at cover, under fire, and holding a contact -- and stood there crouched behind nothing.
        /// </summary>
        public void OnRespawn(ServerPlayer mob)
        {
            if (!brains.TryGetValue(mob.Id, out var brain)) return;

            var squad = BoardFor(mob, brain);
            if (squad.TryGetResourceObjective(mob.Id, out _))
                squad.SetResourceObjective(null);
            mob.OperatingObjectId = 0;
            ClearCover(mob.Id, brain, squad);
            brain.ClearCombatTarget();
            brain.ClearGunshots();
            brain.ClearUnderFire();
            brain.NextProneTick = 0;
            brain.Contacts.Forget();
            brain.MovingSinceTick = 0;
            brain.BoundIndex = 0;
            brain.ResetEntrenchmentHistory();
            brain.AssaultDashActive = false;
            brain.NextCoverQueryTick = 0;
            brain.ObjectiveReached = false;
            // Cancel by actor as well as clearing the agent's bookkeeping. Cover and objective
            // requests can replace one another, so the worker's latest generation is the authority;
            // cancelling only the request the agent happened to remember could leave a pre-
            // relocation job alive and able to suppress the first request from the new spawn.
            navigation.Cancel(mob.Id);
            brain.Navigation.Clear();
            homes[mob.Id] = mob.Position;
            brain.ObjectiveRevision = squad.ObjectiveRevision;
            brain.Navigation.SetDestination(
                squad.TryGetObjective(out var objective)
                    ? ObjectiveDestination(mob.Id, objective.Position, squad)
                    : RandomSurfacePoint(mob.Position));
        }

        public void Dispose()
        {
            // A snapshot must not outlive the server that made it: actor ids repeat across sessions,
            // so a stale one would label the next session's NPCs with the last one's decisions, and
            // draw them the last one's routes.
            MobDebugFeed.Clear();
            MobPathFeed.Clear();
            navigation.Dispose();
        }

        public bool TryDequeueStuckMob(out ushort mobId)
            => stuckMobs.TryDequeue(out mobId);

        /// <summary>
        /// Re-forms this NPC's brain on the side its actor now belongs to.
        ///
        /// A fresh brain rather than an edited one, because MobBrain.Team is init-only and should
        /// stay that way: a brain holds a squad index, cover leases, a bound in progress and a
        /// believed set of contacts, and every one of those is a fact about the side it was formed
        /// on. Releasing it through the old board and building a new one puts him back through the
        /// same path a newly spawned NPC takes — unsquadded, and picked up by the next
        /// <see cref="ReformSquads"/> pass with whoever he is now standing beside.
        /// </summary>
        public void ChangeTeam(ServerPlayer mob)
        {
            navigation.Cancel(mob.Id);
            if (brains.Remove(mob.Id, out var previous))
            {
                BoardFor(previous).Release(mob.Id);
                previous.Navigation.Clear();
            }
            brains[mob.Id] = CreateBrain(mob.Team, mob.Position, mob.Id);
            brains[mob.Id].Navigation.SetDestination(RandomSurfacePoint(mob.Position));
        }

        public void RemoveMob(ServerPlayer mob)
        {
            navigation.Cancel(mob.Id);
            if (brains.Remove(mob.Id, out var brain))
            {
                BoardFor(brain).Release(mob.Id);
                brain.Navigation.Clear();
            }
            homes.Remove(mob.Id);
        }

        /// <summary>
        /// Re-groups every team's living NPCs by proximity. Squads were fixed at spawn before this: the
        /// index came from a monotonic counter that never decremented, so a squad could never re-form and
        /// a replacement NPC became a permanent squad of one with its own objective.
        /// </summary>
        private void ReformSquads(uint tick, ICollection<ServerPlayer> actors)
        {
            if (tick % SquadReformTicks != 0) return;

            membersByTeam.Clear();
            foreach (var actor in actors)
            {
                if (!actor.IsMob
                    || actor.Status is { Health.Current: 0 }
                    || actor.Team <= 0
                    || !brains.TryGetValue(actor.Id, out var brain))
                    continue;
                if (!membersByTeam.TryGetValue(actor.Team, out var team))
                    membersByTeam[actor.Team] = team = [];
                team.Add(new SquadMember(actor.Id, brain.SquadIndex, actor.Position));
            }

            rosterScratch.Clear();
            foreach (var pair in membersByTeam)
            {
                SquadFormation.Plan(pair.Value, squadAssignments);

                // TWO passes on purpose, and the split is load-bearing.
                //
                // These were one loop, and one loop meant one `continue` could skip both jobs at
                // once. It did: a guard meant to stop a mid-bound man being REASSIGNED also skipped
                // his ROSTER insertion, so every moving man silently left his squad, got no order,
                // fell through to cover movement, stopped moving, rejoined, was ordered to bound, and
                // was dropped again. Measured, 1.7% of actor-ticks bounding against 97% of movement
                // going to cover seeking.
                //
                // Deciding which squad a man is in and recording that he is in it are different
                // questions. Pass one may decline to change an answer; pass two has no `continue` and
                // therefore cannot lose anybody.

                foreach (var member in pair.Value)
                {
                    if (!squadAssignments.TryGetValue(member.ActorId, out int squadIndex)
                        || !brains.TryGetValue(member.ActorId, out var brain))
                        continue;

                    // A man mid-bound keeps his CURRENT squad rather than being reassigned by
                    // proximity, so a replan cannot change his bearing and bound index under him
                    // while he is crossing open ground.
                    if (brain.MovingSinceTick != 0
                        // The first consumed bounded prefix already has a successor in flight.
                        // Re-forming the squad at that point changes the destination and cancels
                        // it; dense spawn groups then repeat one short prefix forever and never
                        // reach the four-attempt "trap" threshold that used to protect them.
                        || brain.Navigation.HasBegunPartialRecovery
                        || brain.SquadIndex == squadIndex)
                        continue;

                    // Leases belong to the squad that granted them.
                    BoardFor(brain).Release(member.ActorId);
                    // Revisions are scoped to a blackboard. Two squads can both be at revision 3
                    // while owning different objectives, so carrying the numeric revision across a
                    // transfer can retain the old route under the new squad's flag id.
                    navigation.Cancel(member.ActorId);
                    // The route is squad-owned, but recently consumed local prefixes describe the
                    // terrain basin. Preserve those across re-formation or a wall straggler starts
                    // the same short-path cycle each time its squad index changes.
                    brain.Navigation.Clear(preservePartialBacktrack: true);
                    brain.ObjectiveRevision = uint.MaxValue;
                    brain.ObjectiveReached = false;
                    brain.SquadIndex = squadIndex;
                }

                foreach (var member in pair.Value)
                {
                    if (!brains.TryGetValue(member.ActorId, out var brain)) continue;

                    var key = (pair.Key, brain.SquadIndex);
                    if (!rosterScratch.TryGetValue(key, out var roster))
                        rosterScratch[key] = roster = ([], Vector3.Zero);
                    roster.Members.Add(member.ActorId);
                    rosterScratch[key] = (roster.Members, roster.Sum + member.Position);
                }
            }

            foreach (var pair in rosterScratch)
            {
                if (!squads.TryGetValue(pair.Key, out var squad))
                    squads[pair.Key] = squad = new SquadBlackboard();
                squad.SetRoster(
                    pair.Value.Members,
                    pair.Value.Sum / pair.Value.Members.Count);
            }

            // Squads nobody belongs to any more must go, or the commander keeps planning for ghosts.
            emptySquads.Clear();
            foreach (var pair in squads)
                if (!rosterScratch.ContainsKey(pair.Key))
                    emptySquads.Add(pair.Key);
            foreach (var key in emptySquads)
                squads.Remove(key);
        }

        /// <summary>
        /// Assigns base-of-fire and bound roles per squad. Replanning is deliberately slower than the
        /// tick rate: roles that flip every tick are indistinguishable from no roles at all.
        /// </summary>
        private void PlanSquadTactics(uint tick, ICollection<ServerPlayer> actors)
        {
            if (tick % TacticsReplanTicks != 0) return;

            foreach (var pair in squads)
            {
                var squad = pair.Value;
                bool hasThreat = squad.TryGetPrimaryThreat(tick, out var threat);
                // The threat's weapon is what makes the range matchup decidable, so it is read from
                // the believed contact rather than assumed. An unidentified threat is costed as a
                // carbine: the middle of the range, and the safe error in both directions.
                ItemType threatWeapon = threat.ObservedWeapon
                    ?? ItemConfig.UnidentifiedThreatWeapon;

                tacticalInputs.Clear();
                foreach (ushort actorId in squad.Roster)
                {
                    if (!brains.TryGetValue(actorId, out var brain)) continue;
                    var actor = actors.FirstOrDefault(candidate => candidate.Id == actorId);
                    if (actor is null || actor.Status is { Health.Current: 0 }) continue;

                    tacticalInputs.Add(new SquadMemberState(
                        actorId,
                        actor.Position,
                        weapons.TryGetPrimaryWeapon(actor, out var primary)
                            ? primary.Item.Type
                            : ItemConfig.UnidentifiedThreatWeapon,
                        // How exposed HE is, not how exposed his target is. Passing PerceivedExposure
                        // here told a squad it was protected whenever the man it was shooting at
                        // happened to be behind cover, so holding scored brilliantly, moving scored
                        // terribly, and six men would dig in against one rifle rather than flank it.
                        brain.SelfExposure,
                        brain.SkillFactor,
                        brain.BoundIndex,
                        brain.MovingSinceTick,
                        brain.BoundBearing));
                }
                if (tacticalInputs.Count == 0) continue;

                SquadTactics.Plan(
                    new SquadPlanInput(
                        threat.Position,
                        hasThreat,
                        threatWeapon,
                        CombatValue.DefaultAggression,
                        tick),
                    tacticalInputs,
                    tacticalOrders);
                squad.SetOrders(tacticalOrders);
                foreach (var order in tacticalOrders)
                    if (brains.TryGetValue(order.ActorId, out var brain))
                    {
                        if (order.Role == SquadRole.Bound)
                        {
                            if (brain.MovingSinceTick == 0)
                            {
                                brain.MovingSinceTick = tick;
                                BoundsStarted++;
                                timingBoundsAttempted++;
                            }
                            brain.BoundBearing = brain.BoundBearing.Renew(
                                order.Bearing, tick, SquadTactics.BearingCommitmentTicks);
                        }
                        else
                        {
                            brain.MovingSinceTick = 0;
                            brain.BoundBearing = brain.BoundBearing.Released();
                        }

                        // Contact broken. The next fight opens at full standoff rather than resuming a
                        // closing sequence against an enemy that is no longer there.
                        if (order.Role == SquadRole.None)
                        {
                            brain.BoundIndex = 0;
                            brain.AssaultDashActive = false;
                        }
                    }
            }
        }

        private void CancelPending(
            ushort mobId,
            NavigationAgent agent,
            bool? forCover = null)
        {
            if (agent.CancelPending(forCover) is { } requestId)
                navigation.Cancel(mobId, requestId);
        }

        /// <summary>
        /// Moves a bounding man onto his assigned envelope position. This exists because the cover path
        /// treats <see cref="MobBrain.AtCover"/> as terminal -- an NPC that had arrived somewhere never
        /// moved again unless the threat shifted five metres -- which is why nothing ever leapfrogged.
        /// A bound reuses the cover-destination lane deliberately: claims, path priority, and arrival
        /// detection are all the same problem as moving to a fighting position.
        /// </summary>
        /// <param name="digging">Whether this tick swung the shovel. Reported rather than written onto
        /// mob.State: Apply composes the actor's flags from the action alone, so a `|=` inside a
        /// helper would be silently discarded.</param>
        private void UpdateBoundMovement(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            SquadTacticalOrder order,
            bool closesToFight,
            uint tick,
            out Vector3 intent,
            out bool jump,
            out bool digging)
        {
            digging = false;
            intent = Vector3.Zero;
            jump = false;

            bool retarget =
                !brain.HasCoverDestination
                || brain.CoverKind != CoverKind.Advance
                || HorizontalDistanceSquared(brain.CoverDestination, order.Destination)
                    > BoundArrivalDistance * BoundArrivalDistance;
            if (retarget)
            {
                ClearCover(mob.Id, brain, squad);
                if (!TryCellAt(order.Destination, out var cell))
                {
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    return;
                }
                Vector3 destination = NavTraversal.Position(terrain, cell);
                if (!squad.TryClaim(mob.Id, destination, tick))
                {
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    return;
                }
                brain.HasCoverDestination = true;
                brain.CoverDestination = destination;
                brain.CoverPeekPosition = destination;
                brain.CoverKind = CoverKind.Advance;
                brain.CoverTerrainVersion = terrain.EditVersion;
                RequestPath(
                    mob,
                    destination,
                    tick,
                    forCover: true,
                    replacePending: true,
                    allowJump: true,
                    allowDig: true);
                return;
            }

            squad.RefreshClaim(mob.Id, tick);
            if (TryClearNavigationHeadroom(
                    mob,
                    brain,
                    brain.CoverDestination,
                    tick,
                    out intent,
                    out jump,
                    out _,
                    out _))
                return;
            if (HorizontalDistanceSquared(mob.Position, brain.CoverDestination)
                <= BoundArrivalDistance * BoundArrivalDistance)
            {
                // Ground gained. Going set here is what hands the next bound to his partner: he becomes
                // the nearer man, so the plan picks the other one as furthest from the threat.
                CompleteBound(mob, brain, squad, closesToFight, tick);
                return;
            }

            var followState = brain.Navigation.Path.Update(
                mob.Position,
                mob.Move.Grounded,
                terrain.EditVersion,
                // No lane. These follow a route to a cover or entrenchment point chosen for this man
                // specifically; a formation offset would push him off the spot he was sent to.
                Vector3.Zero,
                out intent,
                out jump,
                out var digTarget,
                out var blockedCell);
            brain.Navigation.RememberBlocked(blockedCell);
            if (followState == PathFollowState.Digging)
            {
                intent = Vector3.Zero;
                jump = false;
                digging = true;
                PerformNavigationDig(mob, brain, digTarget, tick);
            }
            else if (followState == PathFollowState.Complete)
            {
                intent = Vector3.Zero;
                jump = false;
                brain.Navigation.Path.Clear();
                CompleteBound(mob, brain, squad, closesToFight, tick);
            }
            else if (followState == PathFollowState.NeedsPath)
            {
                intent = Vector3.Zero;
                jump = false;
                RequestPath(
                    mob,
                    brain.CoverDestination,
                    tick,
                    forCover: true,
                    allowJump: true,
                    allowDig: true);
            }
        }

        /// <summary>
        /// Ends a bound: the man is set, his standoff for the next one is shorter, and the Advance
        /// destination is dropped so the ordinary cover query can find him real cover where he stands.
        /// </summary>
        private static void CompleteBound(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            bool closesToFight,
            uint tick)
        {
            if (closesToFight)
            {
                // The reload bought a dash, not permanent safety. Drop the transit claim and dig a
                // new position here before waiting for the next opening.
                ClearCover(mob.Id, brain, squad);
                brain.AssaultDashActive = false;
                brain.BoundIndex++;
                brain.NextCoverQueryTick = tick;
                return;
            }
            brain.AtCover = true;
            brain.CoverArrivedTick = tick;
            brain.BoundIndex++;
            brain.NextCoverQueryTick = tick;
        }

        /// <inheritdoc cref="UpdateBoundMovement" path="/param[@name='digging']"/>
        private void UpdateCoverMovement(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            bool mayAdvance,
            bool closingDistance,
            bool underFire,
            bool baseOfFire,
            bool startEntrenchment,
            bool closesToFight,
            uint tick,
            out Vector3 intent,
            out bool jump,
            out bool digging)
        {
            digging = false;
            intent = Vector3.Zero;
            jump = false;

            if (!brain.Contacts.TryGet(brain.CombatTargetId, tick, out var primaryThreat))
                return;

            // Execute the decision made above, independent of weapon category. This was still
            // gated on closesToFight after mustEntrench was generalized to every exposed base-of-fire
            // actor. A rifleman could therefore be labelled ENTRENCH (and have firing withheld) while
            // this method ran ordinary cover movement instead, producing the observed crouch/peek
            // deadlock without a single shovel bite.
            //
            // Once begun, finish the fixed plan even if direct sight expires. The origin and grade
            // are fixed when digging begins; deriving either from the falling actor makes the
            // excavation migrate downward with him.
            if ((startEntrenchment || brain.Entrenching)
                && UpdateEntrenchment(
                    mob,
                    brain,
                    squad,
                    primaryThreat,
                    tick,
                    out intent,
                    out jump,
                    out digging))
                return;

            bool invalidated = false;
            bool needsRevalidation = false;
            if (brain.HasCoverDestination)
            {
                bool threatChanged =
                    brain.CoverThreatId != primaryThreat.ActorId
                    || Vector3.DistanceSquared(
                        brain.CoverThreatPosition,
                        primaryThreat.Position)
                        > CoverThreatRequeryDistance * CoverThreatRequeryDistance;
                invalidated = brain.CoverKind == CoverKind.Advance && !closingDistance;
                needsRevalidation = !invalidated && threatChanged;

                if (!invalidated
                    && !brain.Entrenched
                    && brain.CoverTerrainVersion != terrain.EditVersion)
                {
                    if (terrain.GlobalInvalidationVersion > brain.CoverTerrainVersion)
                    {
                        // Reset can replace terrain rather than merely subtract it, so the
                        // monotonic cheap-revalidation argument does not apply.
                        invalidated = true;
                    }
                    else
                    {
                        bool relevantTerrainChanged = CoverTerrainDependency.ChangedSince(
                            terrain,
                            brain.CoverDependencyChunks,
                            brain.CoverTerrainVersion);
                        needsRevalidation |= relevantTerrainChanged;
                        if (!relevantTerrainChanged)
                        {
                            // The edit was unrelated to this choice. Promote the generation so the
                            // dependency list is checked once per edit, not once per subsequent tick.
                            brain.CoverTerrainVersion = terrain.EditVersion;
                        }
                    }
                }

                // Revalidation shares the one-per-tick cover-work budget. A terrain edit can touch
                // several NPC sightlines at once; running all their rays in the editing tick would
                // replace query churn with a larger p99 spike. Unchecked brains retain the old
                // generation and naturally take their turn on following ticks.
                if (needsRevalidation && coverQueriesRemaining > 0)
                {
                    coverQueriesRemaining--;
                    var contacts = brain.Contacts.Snapshot(tick);
                    long coverStarted = Stopwatch.GetTimestamp();
                    bool kept = cover.TryRevalidate(
                        brain.CoverDestination,
                        brain.CoverPeekPosition,
                        brain.CoverKind,
                        contacts,
                        out var refreshed);
                    timingCoverStopwatchTicks += Stopwatch.GetTimestamp() - coverStarted;
                    timingCoverRevalidations++;
                    if (kept)
                    {
                        brain.CoverDestination = refreshed.Position;
                        brain.CoverPeekPosition = refreshed.PeekPosition;
                        brain.CoverTerrainVersion = refreshed.TerrainVersion;
                        brain.CoverDependencyChunks = refreshed.DependencyChunks;
                        brain.CoverThreatId = primaryThreat.ActorId;
                        brain.CoverThreatPosition = primaryThreat.Position;
                        timingCoverRevalidationsKept++;
                        needsRevalidation = false;
                    }
                    else
                    {
                        invalidated = true;
                    }
                }
            }
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
            // A base of fire that is not yet set must be allowed to look, even though it has no wish to
            // advance: finding or digging a fighting position is the whole of its job, and gating the
            // query on mayAdvance alone left a man ordered to hold and shoot standing in the open,
            // never set, so nobody in the squad was ever cleared to bound.
            bool mayQueryCover = mayAdvance || baseOfFire && !brain.AtCover;
            if (mayQueryCover
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
                    requireAdvance: closingDistance
                        && !(closesToFight && baseOfFire && !brain.AtCover));
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
                    brain.CoverDependencyChunks = choice.DependencyChunks;
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
                            tick,
                            forCover: true,
                            replacePending: true,
                            allowDig: true);
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
                    brain.CoverDependencyChunks = CoverTerrainDependency.Capture(
                        advance,
                        advance,
                        primaryThreat.Position);
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    RequestPath(
                        mob,
                        advance,
                        tick,
                        forCover: true,
                        replacePending: true,
                        allowJump: true,
                        allowDig: true);
                }
                else
                {
                    brain.NextCoverQueryTick = tick + CoverRetryTicks;
                    // A base of fire with no usable terrain makes its own. Previously this only ran while
                    // rounds were actually landing, so a man told to hold and shoot from open ground
                    // simply stood in it. Termination is the cover scorer itself: once the cut classifies
                    // the spot as a fighting position the query above accepts it and digging stops, which
                    // is exactly "deep enough to peek over, low enough to crouch behind".
                    if (underFire || baseOfFire)
                    {
                        _ = DigEmergencyCover(mob, primaryThreat.Position, tick, out bool dugCover);
                        digging |= dugCover;
                    }
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
                    jump = desired.Y > mob.Position.Y + 0.2f && mob.Move.Grounded;
                    delta.Y = 0f;
                    if (delta.LengthSquared() > CornerArrivalDistance * CornerArrivalDistance)
                        intent = Vector3.Normalize(delta);
                }
                return;
            }
            if (!mayAdvance && brain.CoverKind == CoverKind.Advance)
                return;

            if (TryClearNavigationHeadroom(
                    mob,
                    brain,
                    brain.CoverDestination,
                    tick,
                    out intent,
                    out jump,
                    out _,
                    out _))
                return;
            var followState = brain.Navigation.Path.Update(
                mob.Position,
                mob.Move.Grounded,
                terrain.EditVersion,
                // No lane. These follow a route to a cover or entrenchment point chosen for this man
                // specifically; a formation offset would push him off the spot he was sent to.
                Vector3.Zero,
                out intent,
                out jump,
                out var digTarget,
                out var blockedCell);
            brain.Navigation.RememberBlocked(blockedCell);
            if (followState == PathFollowState.Digging)
            {
                intent = Vector3.Zero;
                jump = false;
                digging = true;
                PerformNavigationDig(mob, brain, digTarget, tick);
            }
            else if (followState == PathFollowState.Complete)
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
                    tick,
                    forCover: true,
                    allowDig: true);
            }
        }

        /// <inheritdoc cref="UpdateBoundMovement" path="/param[@name='digging']"/>
        private bool UpdateEntrenchment(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            AiContact threat,
            uint tick,
            out Vector3 intent,
            out bool jump,
            out bool digging)
        {
            intent = Vector3.Zero;
            jump = false;
            digging = false;
            if (!brain.Entrenching)
            {
                Vector3 toward = threat.Position - mob.Position;
                toward.Y = 0f;
                if (toward.LengthSquared() <= 1e-6f) return false;
                toward = Vector3.Normalize(toward);
                int gradeX = (int)MathF.Floor(
                    mob.Position.X - toward.X * FoxholeGradeProbeDistance);
                int gradeZ = (int)MathF.Floor(
                    mob.Position.Z - toward.Z * FoxholeGradeProbeDistance);
                if (SurfaceQuery.HighestSurfaceY(terrain, gradeX, gradeZ) is not { } grade)
                    return false;

                ClearCover(mob.Id, brain, squad);
                if (!squad.TryClaim(mob.Id, mob.Position, tick)) return false;
                brain.BeginEntrenchment(mob.Position, toward, grade);
                brain.CoverThreatId = threat.ActorId;
                brain.CoverThreatPosition = threat.Position;
                CancelPending(mob.Id, brain.Navigation, forCover: true);
                brain.Navigation.Path.Clear();
            }

            squad.RefreshClaim(mob.Id, tick);
            if (FoxholePlan.NextBite(
                    terrain,
                    brain.EntrenchOrigin,
                    brain.EntrenchToward,
                    brain.EntrenchGrade) is { } target)
            {
                digging = true;
                PerformNavigationDig(
                    mob,
                    brain,
                    target,
                    tick,
                    plannedExcavation: true);
                return true;
            }

            int centreX = (int)MathF.Floor(brain.EntrenchOrigin.X);
            int centreZ = (int)MathF.Floor(brain.EntrenchOrigin.Z);
            if (!NavTraversal.TryFindStandable(
                    terrain,
                    centreX,
                    centreZ,
                    (int)MathF.Floor(brain.EntrenchGrade),
                    below: 5,
                    above: 1,
                    out var centreCell,
                    out _)
                || !NavTraversal.TryPosition(terrain, centreCell, out var centre))
                return true;

            if (!brain.HasCoverDestination)
            {
                brain.HasCoverDestination = true;
                brain.CoverDestination = centre;
                brain.CoverPeekPosition = TryEntrenchmentPeek(brain, centre, out var peek)
                    ? peek
                    : centre;
                brain.CoverKind = CoverKind.CornerFightingPosition;
                brain.CoverThreatId = threat.ActorId;
                brain.CoverThreatPosition = threat.Position;
                brain.CoverTerrainVersion = terrain.EditVersion;
            }

            Vector3 toCentre = centre - mob.Position;
            float horizontalDistanceSquared = toCentre.X * toCentre.X + toCentre.Z * toCentre.Z;
            bool physicallyInside = horizontalDistanceSquared
                    <= CornerArrivalDistance * CornerArrivalDistance
                && MathF.Abs(mob.Position.Y - centre.Y) <= 0.75f
                && mob.Position.Y <= brain.EntrenchOrigin.Y - 1.25f;
            if (physicallyInside && ProtectedFrom(threat.Position, mob.Position))
            {
                brain.AtCover = true;
                brain.CoverArrivedTick = tick;
                brain.CoverTerrainVersion = terrain.EditVersion;
                brain.CompleteEntrenchment();
                return true;
            }

            if (horizontalDistanceSquared > CornerArrivalDistance * CornerArrivalDistance)
            {
                toCentre.Y = 0f;
                intent = Vector3.Normalize(toCentre);
                jump = centre.Y > mob.Position.Y + 0.2f && mob.Move.Grounded;
            }
            return true;
        }

        private bool TryEntrenchmentPeek(
            MobBrain brain,
            Vector3 centre,
            out Vector3 peek)
        {
            int forwardX;
            int forwardZ;
            if (MathF.Abs(brain.EntrenchToward.X) > MathF.Abs(brain.EntrenchToward.Z))
            {
                forwardX = brain.EntrenchToward.X < 0f ? -1 : 1;
                forwardZ = 0;
            }
            else
            {
                forwardX = 0;
                forwardZ = brain.EntrenchToward.Z < 0f ? -1 : 1;
            }
            int rightX = forwardZ;
            int rightZ = -forwardX;
            int originX = (int)MathF.Floor(brain.EntrenchOrigin.X);
            int originZ = (int)MathF.Floor(brain.EntrenchOrigin.Z);
            Vector3? fallback = null;
            float fallbackDistanceSquared = float.PositiveInfinity;
            foreach (var (right, back) in (ReadOnlySpan<(int Right, int Back)>)[
                         (-2, 0), (2, 0), (-1, 1), (1, 1), (0, 1)])
            {
                int x = originX + rightX * right - forwardX * back;
                int z = originZ + rightZ * right - forwardZ * back;
                if (!NavTraversal.TryFindStandable(
                        terrain,
                        x,
                        z,
                        (int)MathF.Floor(brain.EntrenchGrade),
                        below: 4,
                        above: 1,
                        out var cell,
                        out float surfaceY)
                    || surfaceY <= centre.Y + 0.15f
                    || !NavTraversal.TryPosition(terrain, cell, out peek))
                    continue;
                float candidateDistanceSquared = HorizontalDistanceSquared(peek, centre);
                if (candidateDistanceSquared < fallbackDistanceSquared)
                {
                    fallback = peek;
                    fallbackDistanceSquared = candidateDistanceSquared;
                }
                Vector3 eye = peek + Vector3.UnitY * Digging.EyeHeight;
                foreach (float aimHeight in GunConfig.AimHeights)
                {
                    Vector3 aim = brain.CoverThreatPosition + Vector3.UnitY * aimHeight;
                    Vector3 ray = aim - eye;
                    float distance = ray.Length();
                    if (distance <= 1e-5f) continue;
                    if (TerrainRaycast.Cast(terrain, eye, ray / distance, distance) is { } hit
                        && hit.Distance < distance - 0.1f)
                        continue;
                    return true;
                }
            }

            peek = fallback.GetValueOrDefault();
            return fallback.HasValue;
        }

        private bool ProtectedFrom(Vector3 threatFeet, Vector3 actorFeet)
        {
            Vector3 origin = threatFeet + Vector3.UnitY * Digging.EyeHeight;
            foreach (float height in GunConfig.AimHeights)
            {
                Vector3 target = actorFeet + Vector3.UnitY * height;
                Vector3 delta = target - origin;
                float distance = delta.Length();
                if (distance <= 1e-5f) return false;
                var hit = TerrainRaycast.Cast(terrain, origin, delta / distance, distance);
                if (hit is null || hit.Value.Distance >= distance - 0.1f)
                    return false;
            }
            return true;
        }

        private void PerformNavigationDig(
            ServerPlayer mob,
            MobBrain brain,
            Vector3 target,
            uint tick,
            bool plannedExcavation = true)
        {
            mob.Hotbar = HotbarSlot.Shovel;
            // Aim only. Callers report the swing through their `digging` out-parameter, which the
            // action carries; a `|= Shooting` here would be composed away by Apply.
            FaceDigTarget(mob, target, NetworkConfig.FixedDt);
            long versionBeforeDig = terrain.EditVersion;
            terrainEdits.ApplyDig(
                mob,
                new PlayerDigData
                {
                    Target = target,
                    Hotbar = HotbarSlot.Shovel,
                },
                tick,
                plannedExcavation);
            if (terrain.EditVersion != versionBeforeDig)
            {
                brain.Navigation.RememberDigSite(target, tick);
                BoardFor(mob, brain).LeaseExcavation(mob.Id, target, tick);
            }
        }

        private static bool ShouldCrouchAtCover(MobBrain brain, uint tick)
        {
            if (brain.CoverKind == CoverKind.Concealment) return true;
            uint elapsed = tick - brain.CoverArrivedTick;
            uint cycle = CoverCrouchTicks + CoverStandTicks;
            return elapsed % cycle < CoverCrouchTicks;
        }

        /// <param name="swung">Whether a shovel bite was actually taken. Distinct from the return
        /// value, which means "this actor is digging emergency cover" and is true on cooldown ticks
        /// where nothing moved.</param>
        private bool DigEmergencyCover(
            ServerPlayer mob,
            Vector3 threatPosition,
            uint tick,
            out bool swung)
        {
            swung = false;
            Vector3 toward = threatPosition - mob.Position;
            toward.Y = 0f;
            if (toward.LengthSquared() > 1e-6f)
                toward = Vector3.Normalize(toward);

            // Grade is measured BEHIND the actor, outside its own excavation. Measuring it where it
            // stands means the hole defines its own grade and the digging never terminates -- an NPC
            // under sustained fire used to bite the same spot until it stood in a pit it could
            // neither jump out of nor, digging being off for combat paths, excavate its way out of.
            int gradeX = (int)MathF.Floor(mob.Position.X - toward.X * FoxholeGradeProbeDistance);
            int gradeZ = (int)MathF.Floor(mob.Position.Z - toward.Z * FoxholeGradeProbeDistance);
            if (SurfaceQuery.HighestSurfaceY(terrain, gradeX, gradeZ) is not { } grade) return false;

            // The SHAPE of the position lives in FoxholePlan -- hole first, then widen, never the
            // parapet. This used to be a single probe half a metre in front, which cut a post-hole:
            // deep enough to pass a depth check and too narrow to take cover in.
            if (FoxholePlan.NextBite(terrain, mob.Position, toward, grade) is not { } target)
                return false;
            if (tick < mob.NextDigTick) return true;

            swung = true;
            mob.Hotbar = HotbarSlot.Shovel;
            // Aim, not output: the shovel-swing flag is reported through the action instead, because
            // Apply composes mob.State from the action alone and would discard a `|=` written here.
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
            return true;
        }

        /// <summary>
        /// The one movement authority for mobs, timed. Every mob intent goes through here so the
        /// solver's share of the tick is measured rather than inferred — see
        /// <see cref="timingSolverStopwatchTicks"/> for why that distinction cost real work.
        /// </summary>
        private bool TimedClearNavigationHeadroom(
            ServerPlayer mob,
            MobBrain brain,
            Vector3 destination,
            uint tick,
            out Vector3 intent,
            out bool jump,
            out bool digging,
            out bool terrainProgress)
        {
            long started = Stopwatch.GetTimestamp();
            bool recovered = TryClearNavigationHeadroom(
                mob,
                brain,
                destination,
                tick,
                out intent,
                out jump,
                out digging,
                out terrainProgress);
            timingHeadroomStopwatchTicks += Stopwatch.GetTimestamp() - started;
            return recovered;
        }

        /// <summary>
        /// The action's flags, and ONLY the action's flags.
        ///
        /// Composed from None rather than merged onto mob.State deliberately. Three of the five old
        /// exits merged (`mob.State.With(...)`) and two replaced (`PlayerStateFlags.None.With(...)`),
        /// so whether a flag survived a tick depended on which branch produced it. If a decider wants
        /// a flag it says so.
        /// </summary>
        internal static void ComposeState(in MobAction action, out PlayerStateFlags state)
            => state = PlayerStateFlags.None
                .With(PlayerStateFlags.Moving, action.Intent != Vector3.Zero)
                .With(PlayerStateFlags.Jumping, action.Jump)
                .With(PlayerStateFlags.Sprinting, action.Sprint)
                .With(PlayerStateFlags.Crouching, action.Crouch && !action.Prone)
                .With(PlayerStateFlags.Prone, action.Prone)
                .With(PlayerStateFlags.Shooting, action.Shooting)
                .With(PlayerStateFlags.Aiming, action.Aiming)
                .With(PlayerStateFlags.Reloading, action.Reloading);

        internal static bool ShouldGoProne(
            Vector3 intent,
            bool underFire,
            bool atCover,
            bool digging,
            bool engaging,
            float engagementRange,
            uint tick,
            uint nextProneTick)
            => intent == Vector3.Zero
               && underFire
               && !atCover
               && !digging
               && engaging
               && engagementRange >= ProneMinimumEngagementRange
               && tick >= nextProneTick;

        /// <summary>
        /// The one place an NPC's tick output reaches the actor. Every decider returns a MobAction;
        /// this is what makes it real.
        /// </summary>
        private void Apply(ServerPlayer mob, in MobAction action, float dt)
        {
            ComposeState(action, out var state);
            mob.State = state;
            if (action.TurnTo)
            {
                mob.Yaw = action.Yaw;
                mob.Pitch = action.Pitch;
            }
            mob.LastIntent = action.Intent;
            StepSolver(mob, action.Intent, dt);
        }

        private void StepSolver(ServerPlayer mob, Vector3 intent, float dt)
        {
            long started = Stopwatch.GetTimestamp();
            // Weapon weight slows an NPC exactly as it slows a player — one movement path, and the
            // navigation cost model is the only thing that does not know about it, which is
            // survivable because a slower actor arrives late rather than wrong.
            PlayerMovement.Step(
                terrain, ref mob.Move, intent, mob.State, dt, items.MoveSpeedScale(mob, mob.Hotbar));
            timingSolverStopwatchTicks += Stopwatch.GetTimestamp() - started;
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
            double solverUsPerTick =
                timingSolverStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double combatUsPerTick =
                timingCombatStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double entrenchUsPerTick =
                timingEntrenchStopwatchTicks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double Us(long ticks) => ticks * 1_000_000d / Stopwatch.Frequency / timingTicks;
            double headroomUs = Us(timingHeadroomStopwatchTicks);
            double followerUs = Us(timingFollowerStopwatchTicks);
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
            double stationaryFraction = timingActorSamples == 0
                ? 0d
                : timingStationaryActorTicks / (double)timingActorSamples;
            double editsPerNpcMinute = agents <= 0d
                ? 0d
                : timingTerrainEdits * 60d / agents;
            double engagementRange = timingEngagementSamples == 0
                ? 0d
                : timingEngagementRangeMetres / timingEngagementSamples;
            double requestsPerSquadMinute = squads.Count == 0
                ? 0d
                : requests * 60d / squads.Count;
            double orchestrationUs = Math.Max(
                0d,
                movementUsPerTick - solverUsPerTick - combatUsPerTick - entrenchUsPerTick);

            latestStats = FormattableString.Invariant(
                $"AI 1s avg: agents {agents:0.0}; stationary {stationaryFraction:P1}; terrain edits {editsPerNpcMinute:0.0}/NPC/min; bounds {timingBoundsCompleted}/{timingBoundsAttempted} completed/attempted; engagement {engagementRange:0.0} m avg; movement {movementUsPerTick:0.0} us/tick (solver {solverUsPerTick:0.0} | combat {combatUsPerTick:0.0}, entrench {entrenchUsPerTick:0.0}, orchestration {orchestrationUs:0.0} [headroom {headroomUs:0.0}, path-follower {followerUs:0.0}]); perception {perceptionUsPerTick:0.0} us/tick; cover {coverUsPerTick:0.0} us/tick ({timingCoverQueries} searches; revalidate {timingCoverRevalidationsKept}/{timingCoverRevalidations} kept); {navigation.WorkerCount} path workers {pathUsPerTick:0.0} aggregate us/tick off-thread; paths {requests} requested ({requestsPerSquadMinute:0.0}/squad/min), {completed} completed ({complete} full/{partial} partial), queue {queueUsPerPath:0} us/path p50/p95 {queueP50}/{queueP95} us, search p50/p95 {searchP50}/{searchP95} us, {nodesPerPath:0} nodes/path, {metresPerPath:0.0} m/path, traversal cache {cacheHits} hits, shared routes {sharedReuses}, {cancelled} cancelled, {invalidated} spatially invalidated");

            timingTicks = 0;
            timingAgentSamples = 0;
            timingActorSamples = 0;
            timingStationaryActorTicks = 0;
            timingTerrainEdits = 0;
            timingBoundsAttempted = 0;
            timingBoundsCompleted = 0;
            timingEngagementRangeMetres = 0d;
            timingEngagementSamples = 0;
            timingMovementStopwatchTicks = 0;
            timingSolverStopwatchTicks = 0;
            timingCombatStopwatchTicks = 0;
            timingEntrenchStopwatchTicks = 0;
            timingHeadroomStopwatchTicks = 0;
            timingFollowerStopwatchTicks = 0;
            timingPerceptionStopwatchTicks = 0;
            timingCoverStopwatchTicks = 0;
            timingCoverQueries = 0;
            timingCoverRevalidations = 0;
            timingCoverRevalidationsKept = 0;
            timingNavigationQueueUs.Clear();
            timingNavigationSearchUs.Clear();
        }

        /// <summary>
        /// Hands the last tick's decisions to the overlay. At the top of the tick rather than the
        /// bottom because <see cref="Step"/> is driven per actor by <see cref="GameWorld"/> and there
        /// is no "all mobs have stepped" moment in here to publish from — a tick of lag on a label a
        /// human is reading costs nothing, and one publish site cannot disagree with itself.
        /// </summary>
        private void PublishDebugStates(ICollection<ServerPlayer> actors)
        {
            if (MobDebugFeed.Enabled)
            {
                var states = new Dictionary<ushort, string>(actors.Count);
                foreach (var actor in actors)
                    if (actor.IsMob && brains.TryGetValue(actor.Id, out var brain))
                        states[actor.Id] = brain.DebugIntent;
                MobDebugFeed.Publish(states);
            }

            if (!MobPathFeed.Enabled) return;

            // A fresh snapshot every tick rather than a mutated one: the reader is on another
            // thread, and an immutable answer swapped in one write is the whole reason this is safe.
            var paths = new Dictionary<ushort, IReadOnlyList<MobPathPoint>>(actors.Count);
            foreach (var actor in actors)
            {
                if (!actor.IsMob || !brains.TryGetValue(actor.Id, out var brain)) continue;
                var route = brain.Navigation.Path.RemainingWaypoints
                    .Select(waypoint => new MobPathPoint(waypoint.Position, waypoint.Action))
                    .ToArray();
                if (route.Length > 0) paths[actor.Id] = route;
            }
            MobPathFeed.Publish(paths);
        }

        public string Stats() => latestStats;

        /// <summary>
        /// Echoes the same one-second window to stdout. `ai stats` needs somebody typing at the
        /// right moment, which a profiling run cannot rely on.
        /// </summary>
        public void LogStats() => Console.WriteLine("[AI]: " + latestStats);

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

        /// <summary>
        /// A new NPC starts unsquadded. The next <see cref="ReformSquads"/> pass puts it with whoever it
        /// is actually standing next to, which is what a monotonic per-team counter could never do.
        /// </summary>
        private MobBrain CreateBrain(int team, Vector3 home, ushort actorId)
        {
            int squadIndex = 0;
            _ = squads.TryAdd((team, squadIndex), new SquadBlackboard());
            return new MobBrain
            {
                Team = team,
                SquadIndex = squadIndex,
                SkillFactor = Marksmanship.SkillFactorFor(mobSeed, actorId),
            };
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
            uint tick,
            bool forCover,
            bool replacePending = false,
            bool allowDig = false,
            bool allowJump = false,
            NavigationPriority priority = NavigationPriority.Objective)
        {
            var navigationAgent = brains[mob.Id].Navigation;
            if (navigationAgent.HasPending(forCover) && !replacePending)
                return true;
            if (priority == NavigationPriority.Prefetch
                && !navigationAgent.CanPrefetch(tick))
                return true;
            // A grounded capsule can straddle a trench lip, wall foot, or authored spawn edge while
            // its centre's exact X/Z column is not itself standable. Ordinary navigation must recover
            // from the nearest capsule-valid cell as it did before; requiring the exact actor column
            // here leaves the follower with no way to replan precisely when it reaches an obstacle.
            // TryActorCell remains the intentionally strict test for clearance/escape excavation,
            // where borrowing an unrelated nearby or overhead surface would target the wrong soil.
            if (!TryCellAt(mob.Position, out var start)
                // Excavation can remove the exact formation/flag sample. Resolve the destination
                // over a wider local ring so the actor routes to intact grade beside its cut rather
                // than becoming pathless directly underneath the original point.
                || !NavTraversal.TryFindNearestStandableGoal(
                    terrain,
                    destination,
                    horizontalRadius: 8,
                    out var target))
            {
                DiagFor(mob.Id).Refused++;
                return false;
            }
            if (priority == NavigationPriority.Prefetch)
            {
                NavCell? partialStart = navigationAgent.Path.PartialStartCell;
                if (HasLeftPartialTrap(mob.Position, destination, partialStart))
                    navigationAgent.ClearPartialBacktrack();
                else
                    navigationAgent.RememberPartialBacktrack(partialStart);
            }
            float destinationRise = destination.Y - mob.Position.Y;
            float destinationRange = HorizontalDistance(mob.Position, destination);
            if (!forCover
                && destinationRise >= 5f
                // A large rise with a shallow global bearing is a pit: the local wall needs narrow
                // staircase bites that preserve its tread. A genuinely steep destination (a soil
                // ramp) keeps the broader planned brush so clearance progresses across the face.
                && destinationRise < destinationRange * 0.5f)
                navigationAgent.CommitPreciseExcavation();
            long? blockedCellKey = navigationAgent.TakeAvoidedCell();
            IReadOnlyList<long> partialBacktrackCellKeys =
                navigationAgent.PartialBacktrackCellKeys;
            NavCell? preferredDigSite = navigationAgent.PreferredDigSite(tick);
            bool excavationLeasedByOther = BoardFor(mob, brains[mob.Id])
                    .IsExcavationLeasedByOther(mob.Id, mob.Position, tick)
                // A lease coordinates useful work; it is not a permanent veto. If following the
                // owner's cut has produced no actor or terrain progress for five seconds, this
                // member may solve its own edge. That preserves one staircase in the normal case
                // while preventing one bad lease from pinning the rest of a squad for the entire
                // twenty-second lease window (or forever while its owner keeps refreshing it).
                && !navigationAgent.Progress.HasStalledFor(
                    mob.Position,
                    tick,
                    5u * NetworkConfig.TickRate);
            bool recoveringFromBlockedEdge = blockedCellKey is not null;
            long sharedRouteKey = 0;
            var brain = brains[mob.Id];
            if (!forCover
                && BoardFor(brain).TryGetObjective(out var objective)
                && blockedCellKey is null
                && preferredDigSite is null
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
                // A jump that the live follower failed is stronger evidence than the idealized
                // centre-of-cell simulation that originally admitted it. For the next few replans,
                // remove jump edges entirely and let the dig escalation produce another stair tread
                // instead of selecting a different theoretical jump and hopping at the wall again.
                allowJump: (!forCover || allowJump) && !recoveringFromBlockedEdge,
                // Every cover request used to force this off, so an NPC in a firefight -- which is
                // exactly when it is in a trench -- could never dig, because combat owns movement and
                // the objective path that permits digging never runs. The caller decides now; short
                // reposition requests still leave it at its default of false.
                allowDig: allowDig && !excavationLeasedByOther,
                blockedCellKey: blockedCellKey,
                partialBacktrackCellKeys: partialBacktrackCellKeys,
                partialBacktrackAttempts: navigationAgent.PartialBacktrackAttempts,
                sharedRouteKey: sharedRouteKey,
                priority: forCover ? NavigationPriority.Combat : priority,
                preferredDigSite: preferredDigSite);
            if (requestId != 0)
            {
                DiagFor(mob.Id).Requests++;
                navigationAgent.RecordRequest(requestId, forCover);
                if (priority == NavigationPriority.Prefetch)
                    navigationAgent.RecordPrefetch(tick);
            }
            return requestId != 0;
        }

        /// <summary>
        /// How far above its feet a resolved start cell may sit. One traversal step: a cell the actor
        /// could not reach by stepping is not a cell the actor is standing on.
        /// </summary>
        private const float MaximumStartRise = NavTraversal.MaximumTraverseCellDelta;

        private bool TryCellAt(
            Vector3 position,
            out NavCell cell,
            int horizontalRadius = 3)
        {
            // The actor's own column is the truth whenever it answers; the neighbourhood search is
            // the recovery for a capsule straddling a trench lip or wall foot.
            if (TryActorCell(position, out cell))
                return true;
            if (!NavTraversal.TryFindNearestStandable(
                    terrain,
                    position,
                    horizontalRadius,
                    out cell))
                return false;
            // That search takes the FIRST cell that answers — six cells UP before one cell down —
            // and then falls back to the column's HIGHEST surface. An actor that has just dug into a
            // wall is standing UNDER an overhang with no headroom of its own, so both rules resolve
            // to the roof: measured 9.8 m above the actor, which planned a perfectly good route from
            // up there and handed the follower a first waypoint it tried to walk straight up to.
            if (IsPlausibleStart(cell)) return true;

            // Refusing outright is worse than the roof: it leaves the actor with no request at all,
            // which measured 1,817 refusals against 22 requests for one man and parked him. So pay
            // for the exhaustive nearest-in-3D resolution HERE, where it is rare, rather than on
            // every request — that is the cost its own documentation says live navigation cannot
            // afford per tick.
            return NavTraversal.TryFindNearestStandableForActor(
                    terrain,
                    position,
                    horizontalRadius,
                    out cell)
                && IsPlausibleStart(cell);

            bool IsPlausibleStart(NavCell candidate)
                => NavTraversal.TryPosition(terrain, candidate, out var resolved)
                   && resolved.Y - position.Y <= MaximumStartRise;
        }

        private bool TryActorCell(Vector3 position, out NavCell cell)
        {
            int x = (int)MathF.Floor(position.X);
            int z = (int)MathF.Floor(position.Z);
            if (!NavTraversal.TryFindStandable(
                    terrain,
                    x,
                    z,
                    (int)MathF.Floor(position.Y),
                    below: 3,
                    above: 3,
                    out cell,
                    out float surfaceY))
                return false;
            return MathF.Abs(surfaceY - position.Y) <= 0.75f;
        }

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

        /// <summary>
        /// This member's wedge lane relative to the squad's objective. The offset rotates with the
        /// approach; point and unrostered actors stay on the route.
        /// </summary>
        private static Vector3 WedgeLateralOffset(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad)
        {
            int slot = -1;
            for (int i = 0; i < squad.Roster.Count; i++)
                if (squad.Roster[i] == mob.Id) { slot = i; break; }
            if (slot <= 0) return Vector3.Zero;

            var toObjective = brain.Navigation.Destination - mob.Position;
            toObjective.Y = 0f;
            if (toObjective.LengthSquared() < 1e-4f) return Vector3.Zero;

            var forward = Vector3.Normalize(toObjective);
            var right = new Vector3(forward.Z, 0f, -forward.X);

            // Alternating sides, widening with rank: 1 right, 2 left, 3 further right — the same
            // arrangement WedgeFormation makes at the destination, expressed as a lane.
            int rank = (slot + 1) / 2;
            float side = slot % 2 == 1 ? 1f : -1f;
            return right * (side * rank * WedgeFormation.SpacingFor(squad.Roster.Count));
        }

        private Vector3 ObjectiveDestination(ushort mobId, Vector3 centre, SquadBlackboard squad)
        {
            int slot = -1;
            for (int i = 0; i < squad.Roster.Count; i++)
                if (squad.Roster[i] == mobId) { slot = i; break; }
            if (slot < 0)
            {
                // Not on a roster yet — the ring is still the right answer for a lone man, and it
                // keeps him off the exact objective point.
                float angle = mobId * GoldenAngle;
                return SurfaceQuery.SurfacePosition(
                    terrain,
                    centre.X + MathF.Cos(angle) * ObjectiveFormationRadius,
                    centre.Z + MathF.Sin(angle) * ObjectiveFormationRadius);
            }

            var slotPosition = WedgeFormation.Slot(centre, squad.Centre, slot, squad.Roster.Count);
            return SurfaceQuery.SurfacePosition(terrain, slotPosition.X, slotPosition.Z);
        }

        private static bool HasLeftPartialTrap(
            Vector3 position,
            Vector3 destination,
            NavCell? partialStart)
        {
            if (partialStart is not { } start) return false;
            var startPosition = new Vector3(start.X + 0.5f, position.Y, start.Z + 0.5f);
            return HorizontalDistance(startPosition, destination)
                 - HorizontalDistance(position, destination)
                >= PartialTrapEscapeDistance;
        }

        private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X;
            float dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
            => MathF.Sqrt(HorizontalDistanceSquared(a, b));

        internal static bool CanRecognizeEnemyReload(
            ServerPlayer observer,
            ServerPlayer enemy)
            => enemy.State.HasFlag(PlayerStateFlags.Reloading)
               && HorizontalDistance(observer.Position, enemy.Position)
                   <= EnemyReloadAwarenessRange;

        /// <summary>
        /// How close an advancing actor tries to get before it stops closing.
        ///
        /// Lives here rather than on CombatBehavior now that the engagement-range table is gone: this
        /// is how far a MOVEMENT should carry, not how far a weapon reaches. Whether to advance at
        /// all is decided by the firing solution; this only bounds how far the advance goes.
        /// </summary>
        private const float CombatAdvanceStandoff = 25f;

        private bool TryCombatAdvancePosition(
            ServerPlayer mob,
            Vector3 threat,
            out Vector3 position)
        {
            position = default;
            Vector3 toward = threat - mob.Position;
            toward.Y = 0f;
            float range = toward.Length();
            if (range <= CombatAdvanceStandoff + 1f)
                return false;

            toward /= range;
            float travel = MathF.Min(
                8f,
                range - CombatAdvanceStandoff);
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
                    if (listener.Id == shot.ShooterId
                        || listener.Status is not { Health.Current: > 0 }
                        || !GunshotHearing.CanHear(
                            listener.Team,
                            listener.Position,
                            shot.ShooterTeam,
                            shot.Position))
                        continue;

                    // The TEAM learns where the shot came from, whoever heard it — a human's ears
                    // count here even though they drive none of the behaviour below, because he is
                    // reading a minimap rather than being told to go and look.
                    //
                    // Located rather than pinpointed: PerceivedPosition's error grows with range, so
                    // a rifle across the field is a bearing and one behind you is a man. The NPC
                    // branch below deliberately keeps the EXACT position, because what it does with
                    // it is walk there, and a wrong destination is a different kind of wrong from a
                    // wrong marker.
                    intel?.Heard(
                        listener.Team,
                        shot.ShooterId,
                        GunshotHearing.PerceivedPosition(
                            listener.Position,
                            shot.Position,
                            unchecked((uint)(shot.ShooterId * 2654435761u + shot.Tick))),
                        shot.Tick);

                    if (!listener.IsMob
                        || !brains.TryGetValue(listener.Id, out var brain))
                        continue;
                    brain.Heard.Hear(
                        shot.ShooterId,
                        shot.Position,
                        Vector3.Distance(listener.Position, shot.Position),
                        shot.Tick);
                }
        }

        /// <summary>This actor as the scoring model sees him: what he is holding and how well.</summary>
        private Combatant SelfCombatant(ServerPlayer actor, MobBrain brain)
            => new(
                weapons.TryGetPrimaryWeapon(actor, out var weapon)
                    ? weapon.Item.Type
                    : ItemConfig.UnidentifiedThreatWeapon,
                0f,
                brain.SkillFactor);

        /// <summary>
        /// Prices the actor's ordinary tactical alternatives in net HP/s. Squad bounds and blast
        /// evasion are decided outside this comparison: one is a joint manoeuvre already scored by
        /// SquadTactics, and the other is immediate survival rather than doctrine.
        /// </summary>
        private ActorIntent ScoreCombatIntent(
            ServerPlayer actor,
            MobBrain brain,
            AiContact threat,
            bool mayEntrench)
        {
            // Abandoning a completed fighting position is not an actor-local reposition. The
            // squad may still order a bound (that precondition is resolved before this method),
            // but without that covering manoeuvre the actor works the protected crouch/peek cycle
            // it already paid to construct. Treating an exposed solo advance as an ordinary cover
            // alternative immediately cleared the foxhole and stranded its owner in the cut.
            if (brain.Entrenched)
                return new ActorIntent.HoldAndFire();

            var self = SelfCombatant(actor, brain);
            float range = HorizontalDistance(actor.Position, threat.Position);
            ItemType threatWeapon = threat.ObservedWeapon
                ?? ItemConfig.UnidentifiedThreatWeapon;

            Engagement At(float candidateRange, SelfExposure exposure)
                => new(
                    candidateRange,
                    threatWeapon,
                    threat.ObservedExtraMoa,
                    brain.PerceivedExposure,
                    exposure,
                    threat.TargetingLikelihood);

            float hold = CombatValue.Score(
                self,
                [At(range, brain.SelfExposure)],
                CombatValue.DefaultAggression);

            float preferred = WeaponEffectiveness.PreferredRange(
                self.Weapon,
                brain.SkillFactor);
            float repositionRange = range > preferred + 2f
                ? MathF.Max(preferred, range - 8f)
                : range;
            var transitExposure = SelfExposure.Of(
                (brain.SelfExposure.Fraction + 0.35f) * 0.5f);
            float reposition = 0.5f * (
                CombatValue.Score(
                    self,
                    [new Engagement(
                        (range + repositionRange) * 0.5f,
                        threatWeapon,
                        threat.ObservedExtraMoa,
                        brain.PerceivedExposure,
                        transitExposure,
                        // An actor crossing alone is the obvious target. SquadTactics prices a
                        // covered bound separately; this local alternative has no suppressor.
                        TheirTargetingLikelihood: 1f)],
                    CombatValue.DefaultAggression)
                + CombatValue.Score(
                    self,
                    // Do not price imaginary cover. Until CoverBehavior has actually supplied a
                    // protected destination, repositioning changes range but ends in the open.
                    // Assuming 35% exposure here made a flat-field advance beat constructing the
                    // foxhole whose protection was real and immediately available.
                    [At(repositionRange, brain.AtCover
                        ? SelfExposure.Of(0.35f)
                        : SelfExposure.Full)],
                    CombatValue.DefaultAggression));

            float entrench = float.NegativeInfinity;
            if (mayEntrench)
            {
                float protectedScore = CombatValue.Score(
                    self,
                    [At(range, MobBrain.EntrenchedSelfExposure)],
                    CombatValue.DefaultAggression);
                // Four seconds of forgone fire amortized over a thirty-second fighting-position
                // horizon. This is a cost in the same HP/s currency, not a second threshold.
                float constructionOpportunity = CombatValue.Dealt(
                    self,
                    [At(range, brain.SelfExposure)]) * (4f / 30f);
                entrench = protectedScore - constructionOpportunity;
            }

            const float commitmentHysteresis = 0.5f;
            if (entrench > hold + commitmentHysteresis
                && entrench >= reposition)
                return new ActorIntent.Entrench();
            if (reposition > hold + commitmentHysteresis)
                return new ActorIntent.SeekCover(MayAdvance: repositionRange < range);
            return new ActorIntent.HoldAndFire();
        }

        /// <summary>
        /// An unseen shooter, as an engagement. His weapon is unknown by construction — he was heard
        /// or felt, not identified — so the model assumes the standard threat rather than the worst
        /// case, which would make every distant crack worth answering.
        /// </summary>
        private static Engagement IncomingFrom(ServerPlayer actor, MobBrain brain, Vector3 from)
            => new(
                HorizontalDistance(actor.Position, from),
                ItemConfig.UnidentifiedThreatWeapon,
                0f,
                TargetExposure.Full,
                brain.SelfExposure,
                1f);

        private void ProcessIncomingFire(
            uint tick,
            ICollection<ServerPlayer> actors)
        {
            while (weapons.TryDequeueSuppression(out var suppression))
            {
                var listener = actors.FirstOrDefault(
                    actor => actor.Id == suppression.TargetId);
                if (listener is null
                    || !listener.IsMob
                    || listener.Team == suppression.ShooterTeam
                    || listener.Status is not { Health.Current: > 0 }
                    || !brains.TryGetValue(listener.Id, out var brain))
                    continue;

                // Being shot at is always true and always worth cover, whoever is doing it.
                brain.MarkUnderFire(tick);
                brain.NextCoverQueryTick = tick;

                // Being shot at by somebody worth WALKING TO is not. This used to publish a
                // squad-wide contact at any range, and a weapon that cannot reach yields a zero
                // firing solution, which is the decision to close — so being outranged was what made
                // a squad abandon its objective and cross the map at a sniper. A harasser it cannot
                // answer is now a reason to get down, not a reason to leave.
                if (!ThreatResponse.IsWorthAnswering(
                        SelfCombatant(listener, brain),
                        IncomingFrom(listener, brain, suppression.ThreatPosition),
                        StrategicValue.TicketsPerSecondPerFlag))
                    continue;

                brain.Contacts.Observe(
                    suppression.ShooterId,
                    suppression.ThreatPosition,
                    suppression.Tick);
                BoardFor(listener, brain).Publish(
                    new AiContact(
                        suppression.ShooterId,
                        suppression.ThreatPosition,
                        suppression.Tick,
                        1f),
                    tick);
            }
        }

        /// <summary>
        /// The yaw that turns this actor toward <paramref name="target"/>, or its current one when
        /// there is nothing to turn toward.
        ///
        /// Returns rather than writes. It was FaceHorizontalTarget and set mob.Yaw itself, which made
        /// it a sixth writer of the actor's output — the thing this split exists to have exactly one
        /// of.
        /// </summary>
        private static float YawTowardHorizontal(
            ServerPlayer mob,
            Vector3 target,
            float dt)
        {
            Vector3 delta = target - mob.Position;
            delta.Y = 0f;
            if (delta.LengthSquared() <= 1e-6f) return mob.Yaw;
            return RotateYawTowards(
                mob.Yaw,
                MathF.Atan2(delta.X, delta.Z),
                TurnRadiansPerSecond * dt);
        }

        private static float NormalizeRadians(float angle)
            => MathF.Atan2(MathF.Sin(angle), MathF.Cos(angle));

    }
}
