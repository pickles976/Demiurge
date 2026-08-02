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

        /// <summary>Far enough behind the digger to sample untouched ground rather than its own hole.</summary>
        private const float FoxholeGradeProbeDistance = 2.5f;
        private const uint ClearanceRecoveryTicks = 3 * NetworkConfig.TickRate;

        private const float IdleScanRadiansPerSecond = 45f * MathF.PI / 180f;

        /// <summary>
        /// Squad membership and the tactical plan both run well under the tick rate. Roles that flip
        /// every tick read as noise rather than as a plan, and re-forming squads mid-bound would throw
        /// away the very plan that spread them out.
        /// </summary>
        private const uint SquadReformTicks = NetworkConfig.TickRate;
        private const uint TacticsReplanTicks = NetworkConfig.TickRate / 2;

        /// <summary>Arrival tolerance for a bound. Looser than cover, since the point is ground gained.</summary>
        private const float BoundArrivalDistance = 2.5f;

        /// <summary>A reload is readable at the same distance its sound curve carries the tell.</summary>
        internal const float EnemyReloadAwarenessRange = 100f;

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
        private readonly Dictionary<int, List<SquadMember>> membersByTeam = new();
        private readonly Dictionary<ushort, int> squadAssignments = new();
        private readonly Dictionary<(int Team, int Squad), (List<ushort> Members, Vector3 Sum)>
            rosterScratch = new();
        private readonly List<(int Team, int Squad)> emptySquads = [];
        private readonly List<SquadTacticalInput> tacticalInputs = [];
        private readonly List<SquadTacticalOrder> tacticalOrders = [];
        private int coverQueriesRemaining;
        private int timingTicks;
        private int timingAgentSamples;
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
                        out bool forCover)
                    || forCover != brain.HasCoverDestination)
                    continue;

                if (result.Path.Waypoints.Count > 0
                    && NavPathTerrain.IsValid(terrain, result.Path))
                {
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
                            result.Path,
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
                brain.ObjectiveReached = false;
                brain.Navigation.Path.Clear();
                brain.Navigation.ResetBlocked();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    hasObjective
                        ? ObjectiveDestination(mob.Id, objective.Position, squad)
                        : RandomSurfacePoint(HomeOf(mob)));
            }
            if (!brain.Navigation.HasDestination)
                brain.Navigation.SetDestination(
                    hasObjective
                        ? ObjectiveDestination(mob.Id, objective.Position, squad)
                        : RandomSurfacePoint(HomeOf(mob)));
            Vector3 destination = brain.Navigation.Destination;

            bool heardGunshot = brain.HasRecentGunshot(tick);
            if (heardGunshot
                && brain.AppliedHeardRevision != brain.HeardRevision)
            {
                brain.AppliedHeardRevision = brain.HeardRevision;
                brain.ObjectiveReached = false;
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
                brain.ObjectiveReached = false;
                brain.Navigation.Path.Clear();
                CancelPending(mob.Id, brain.Navigation, forCover: false);
                brain.Navigation.SetDestination(
                    hasObjective
                        ? ObjectiveDestination(mob.Id, objective.Position, squad)
                        : RandomSurfacePoint(HomeOf(mob)));
                destination = brain.Navigation.Destination;
            }

            if (grenadeCombat.TryThrow(mob, brain, squad, actors, tick))
            {
                brain.Navigation.Progress.Reset();
                mob.State = PlayerStateFlags.Shooting;
                mob.LastIntent = Vector3.Zero;
                StepSolver(mob, Vector3.Zero, dt);
                return;
            }
            mob.Hotbar = HotbarSlot.Primary;
            // Shooting means actuating the CURRENT item. Do not carry a shovel swing through the
            // hotbar transition and make it look like the newly equipped primary fired before
            // CombatBehavior authorized a shot this tick.
            mob.State &= ~PlayerStateFlags.Shooting;

            bool hasOrder = squad.TryGetOrder(mob.Id, out var order);
            bool bounding = hasOrder && order.Role == SquadRole.Bound;
            bool assault = weapons.TryGetActiveWeapon(mob, out var activePrimary)
                && activePrimary.Item.Type == ItemType.Ppsh;
            // A base of fire holds and shoots at where the target is, not only at a target it can
            // currently see. That is what buys the bounding man his move.
            bool suppressing =
                hasOrder
                && order.Role == SquadRole.BaseOfFire
                && brain.IsSet;
            bool holdingForEffectiveRange = assault
                && brain.Contacts.TryNearest(mob.Position, tick, out var nearestContact)
                && CombatBehavior.PrefersToHoldFire(
                    ItemType.Ppsh,
                    HorizontalDistance(mob.Position, nearestContact.Position));
            bool mustEntrench = assault
                && !brain.HasCompletedInitialEntrenchment
                && !brain.Entrenched;
            bool readyAtEntrenchPeek = !brain.Entrenched
                || (!ShouldCrouchAtCover(brain, tick)
                    && HorizontalDistanceSquared(mob.Position, brain.CoverPeekPosition)
                        <= CoverArrivalDistance * CoverArrivalDistance);
            bool mayFire = !holdingForEffectiveRange
                && !mustEntrench
                && readyAtEntrenchPeek
                && squad.TryAcquireEngagement(mob.Id, tick);
            if (holdingForEffectiveRange)
                squad.ReleaseEngagement(mob.Id);
            bool underFire =
                brain.IsUnderFire(tick)
                || mob.Spread.SuppressionMoa > 1f;
            long combatStarted = Stopwatch.GetTimestamp();
            bool combatOwnsTick = combat.Tick(
                mob,
                brain,
                tick,
                dt,
                mayFire && !underFire,
                suppressing && mayFire);
            timingCombatStopwatchTicks += Stopwatch.GetTimestamp() - combatStarted;

            if (combatOwnsTick)
            {
                brain.Navigation.Progress.Reset();
                Vector3 combatIntent;
                bool combatJump;
                if (bounding && !mustEntrench)
                {
                    UpdateBoundMovement(
                        mob,
                        brain,
                        squad,
                        order,
                        assault,
                        tick,
                        out combatIntent,
                        out combatJump);
                }
                else
                {
                    bool wantsAdvance = brain.ShouldCloseDistance || !mayFire || underFire;
                    bool assaultWaitingInPosition = assault
                        && hasOrder
                        && order.Role == SquadRole.BaseOfFire;
                    bool mayAdvance =
                        !assaultWaitingInPosition
                        && (underFire
                            || wantsAdvance && squad.TryAcquireAdvance(mob.Id, tick));
                    if (underFire || !mayAdvance)
                        squad.ReleaseAdvance(mob.Id);
                    UpdateCoverMovement(
                        mob,
                        brain,
                        squad,
                        mayAdvance,
                        brain.ShouldCloseDistance,
                        underFire,
                        hasOrder && order.Role == SquadRole.BaseOfFire,
                        assault,
                        tick,
                        out combatIntent,
                        out combatJump);
                }
                bool crouching = brain.AtCover
                    && !bounding
                    && (mob.State.HasFlag(PlayerStateFlags.Reloading)
                        || ShouldCrouchAtCover(brain, tick));

                // Run when the movement IS the job and shooting is not: bounding across open ground,
                // closing on cover not yet reached, or holding a weapon the squad has not cleared
                // you to use. Never while firing or tucked in — SprintingMoa and the post-sprint
                // penalty mean a man who sprints and shoots does neither well, so the state flags
                // that buy the speed also pay for it.
                bool sprinting = combatIntent != Vector3.Zero
                    && !crouching
                    && !mob.State.HasFlag(PlayerStateFlags.Shooting)
                    && (bounding || !brain.AtCover || !mayFire);

                mob.State = mob.State
                    .With(PlayerStateFlags.Moving, combatIntent != Vector3.Zero)
                    .With(PlayerStateFlags.Jumping, combatJump)
                    .With(PlayerStateFlags.Sprinting, sprinting)
                    .With(PlayerStateFlags.Crouching, crouching);
                mob.LastIntent = combatIntent;
                StepSolver(mob, combatIntent, dt);
                return;
            }

            // Digging a fighting position outlasts direct sight: once the actor drops below grade,
            // the parapet itself hides the target for several seconds. Abandoning on contact expiry
            // made a half-dug hole and sent the NPC roaming. Finish the fixed plan, then cycle from
            // its protected centre to the peek station so perception can reacquire naturally.
            long entrenchStarted = Stopwatch.GetTimestamp();
            if (brain.Entrenching)
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
                    out bool entrenchJump);
                bool entrenchCrouch = brain.Entrenched
                    && ShouldCrouchAtCover(brain, tick);
                mob.State = mob.State
                    .With(PlayerStateFlags.Moving, entrenchIntent != Vector3.Zero)
                    .With(PlayerStateFlags.Jumping, entrenchJump)
                    .With(PlayerStateFlags.Sprinting, false)
                    .With(PlayerStateFlags.Crouching, entrenchCrouch);
                mob.LastIntent = entrenchIntent;
                StepSolver(mob, entrenchIntent, dt);
                return;
            }
            if (brain.Entrenched)
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
                mob.State = PlayerStateFlags.None
                    .With(PlayerStateFlags.Moving, coverIntent != Vector3.Zero)
                    .With(PlayerStateFlags.Jumping, coverJump)
                    .With(PlayerStateFlags.Crouching, tucked);
                mob.LastIntent = coverIntent;
                StepSolver(mob, coverIntent, dt);
                return;
            }
            timingEntrenchStopwatchTicks += Stopwatch.GetTimestamp() - entrenchStarted;

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
                && brain.ObjectiveReached
                && !heardGunshot
                && HorizontalDistanceSquared(mob.Position, objective.Position)
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
                            tick);
                        terrainProgress = terrain.EditVersion != versionBeforeDig;
                        if (terrainProgress)
                        {
                            brain.Navigation.RememberDigSite(digTarget, tick);
                            // The edit invalidates the executor's old collision evidence. Let the
                            // next authoritative search reconsider the changed edge from scratch.
                            brain.Navigation.ResetBlocked();
                        }
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
                                    ? ObjectiveDestination(mob.Id, objective.Position, squad)
                                    : RandomSurfacePoint(HomeOf(mob)));
                            destination = brain.Navigation.Destination;
                            followState = PathFollowState.NeedsPath;
                        }
                        else if (reachedGoal && hasObjective)
                        {
                            brain.ObjectiveReached = true;
                            followState = PathFollowState.Following;
                        }
                        else
                        {
                            // A prefetch was queued from an older point on this partial corridor.
                            // Once the actor consumes the prefix, replace that stale request from
                            // the actual frontier so excavation and newly visible detours compete.
                            replacePendingPath = !reachedGoal;
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
                    brain.ClearGunshot();
                    heardGunshot = false;
                    brain.Navigation.SetDestination(
                        hasObjective
                            ? ObjectiveDestination(mob.Id, objective.Position, squad)
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

            mob.State = PlayerStateFlags.None
                .With(PlayerStateFlags.Moving, intent != Vector3.Zero)
                .With(PlayerStateFlags.Jumping, jump)
                // NOT here. Sprinting belongs to the combat path, where there is something to run
                // from or toward; a squad that runs everywhere reads as panicked rather than urgent,
                // and arrives with PostSprintMoa still spoiling its first three seconds of fire.
                // Shooting reads as "actuating the held item", which is what the client's view of a
                // shovel swings on. Digging is the tool's version of pulling the trigger.
                .With(PlayerStateFlags.Shooting, digging);
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

            StepSolver(mob, intent, dt);
            if (brain.Navigation.Progress.Update(
                    mob.Position,
                    tick,
                    expectedToTravel: !holdingObjective,
                    terrainProgress))
                stuckMobs.Enqueue(mob.Id);
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
            ClearCover(mob.Id, brain, squad);
            squad.ReleaseEngagement(mob.Id);
            squad.ReleaseAdvance(mob.Id);
            brain.ClearCombatTarget();
            brain.ClearGunshot();
            brain.ClearUnderFire();
            brain.Contacts.Forget();
            brain.FlankSide = FlankSide.None;
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

        public void Dispose() => navigation.Dispose();

        public bool TryDequeueStuckMob(out ushort mobId)
            => stuckMobs.TryDequeue(out mobId);

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
                foreach (var member in pair.Value)
                {
                    if (!squadAssignments.TryGetValue(member.ActorId, out int squadIndex)
                        || !brains.TryGetValue(member.ActorId, out var brain))
                        continue;
                    if (brain.SquadIndex != squadIndex)
                    {
                        // Leases belong to the squad that granted them.
                        BoardFor(brain).Release(member.ActorId);
                        brain.SquadIndex = squadIndex;
                    }
                    var key = (pair.Key, squadIndex);
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
                var threatActor = hasThreat
                    ? actors.FirstOrDefault(candidate => candidate.Id == threat.ActorId)
                    : null;
                tacticalInputs.Clear();
                foreach (ushort actorId in squad.Roster)
                {
                    if (!brains.TryGetValue(actorId, out var brain)) continue;
                    var actor = actors.FirstOrDefault(candidate => candidate.Id == actorId);
                    if (actor is null || actor.Status is { Health.Current: 0 }) continue;
                    bool assault = weapons.TryGetPrimaryWeapon(actor, out var primary)
                        && primary.Item.Type == ItemType.Ppsh;
                    bool knowsReload = threatActor is { } enemy
                        && CanRecognizeEnemyReload(actor, enemy);
                    tacticalInputs.Add(new SquadTacticalInput(
                        actorId,
                        actor.Position,
                        brain.FlankSide,
                        brain.BoundIndex,
                        brain.IsSet,
                        IsAssault: assault,
                        ThreatReloading: knowsReload,
                        BoundCommitted: brain.AssaultDashActive));
                }
                if (tacticalInputs.Count == 0) continue;

                SquadTactics.Plan(
                    threat.Position,
                    hasThreat,
                    tacticalInputs,
                    tacticalOrders);
                squad.SetOrders(tacticalOrders);
                foreach (var order in tacticalOrders)
                    if (brains.TryGetValue(order.ActorId, out var brain))
                    {
                        brain.FlankSide = order.Side;
                        bool assault = tacticalInputs.Any(input =>
                            input.ActorId == order.ActorId && input.IsAssault);
                        if (order.Role == SquadRole.Bound && assault)
                            brain.AssaultDashActive = true;
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
        private void UpdateBoundMovement(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            SquadTacticalOrder order,
            bool assault,
            uint tick,
            out Vector3 intent,
            out bool jump)
        {
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
                CompleteBound(mob, brain, squad, assault, tick);
                return;
            }

            var followState = brain.Navigation.Path.Update(
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
                intent = Vector3.Zero;
                jump = false;
                PerformNavigationDig(mob, brain, digTarget, tick);
            }
            else if (followState == PathFollowState.Complete)
            {
                intent = Vector3.Zero;
                jump = false;
                brain.Navigation.Path.Clear();
                CompleteBound(mob, brain, squad, assault, tick);
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
            bool assault,
            uint tick)
        {
            if (assault)
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

        private void UpdateCoverMovement(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            bool mayAdvance,
            bool closingDistance,
            bool underFire,
            bool baseOfFire,
            bool assault,
            uint tick,
            out Vector3 intent,
            out bool jump)
        {
            intent = Vector3.Zero;
            jump = false;

            if (!brain.Contacts.TryGet(brain.CombatTargetId, tick, out var primaryThreat))
                return;

            // Assault base-of-fire units finish one position and physically occupy it before they
            // engage. The origin and grade are fixed when digging begins; deriving either from the
            // falling actor made the excavation migrate downward with him.
            if (((assault
                        && !brain.HasCompletedInitialEntrenchment
                        && !brain.Entrenched)
                    || brain.Entrenching)
                && UpdateEntrenchment(
                    mob,
                    brain,
                    squad,
                    primaryThreat,
                    tick,
                    out intent,
                    out jump))
                return;

            bool invalidated =
                brain.HasCoverDestination
                && (brain.CoverThreatId != primaryThreat.ActorId
                    || Vector3.DistanceSquared(
                        brain.CoverThreatPosition,
                        primaryThreat.Position)
                    > CoverThreatRequeryDistance * CoverThreatRequeryDistance
                    || !brain.Entrenched && brain.CoverTerrainVersion != terrain.EditVersion
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
                        && !(assault && baseOfFire && !brain.AtCover));
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
                        _ = DigEmergencyCover(mob, primaryThreat.Position, tick);
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
                out intent,
                out jump,
                out var digTarget,
                out var blockedCell);
            brain.Navigation.RememberBlocked(blockedCell);
            if (followState == PathFollowState.Digging)
            {
                intent = Vector3.Zero;
                jump = false;
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

        private bool UpdateEntrenchment(
            ServerPlayer mob,
            MobBrain brain,
            SquadBlackboard squad,
            AiContact threat,
            uint tick,
            out Vector3 intent,
            out bool jump)
        {
            intent = Vector3.Zero;
            jump = false;
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
                PerformNavigationDig(mob, brain, target, tick);
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
            uint tick)
        {
            mob.Hotbar = HotbarSlot.Shovel;
            mob.State |= PlayerStateFlags.Shooting;
            FaceDigTarget(mob, target, NetworkConfig.FixedDt);
            long versionBeforeDig = terrain.EditVersion;
            terrainEdits.ApplyDig(
                mob,
                new PlayerDigData
                {
                    Target = target,
                    Hotbar = HotbarSlot.Shovel,
                },
                tick);
            if (terrain.EditVersion != versionBeforeDig)
                brain.Navigation.RememberDigSite(target, tick);
        }

        private static bool ShouldCrouchAtCover(MobBrain brain, uint tick)
        {
            if (brain.CoverKind == CoverKind.Concealment) return true;
            uint elapsed = tick - brain.CoverArrivedTick;
            uint cycle = CoverCrouchTicks + CoverStandTicks;
            return elapsed % cycle < CoverCrouchTicks;
        }

        private bool DigEmergencyCover(
            ServerPlayer mob,
            Vector3 threatPosition,
            uint tick)
        {
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

            mob.Hotbar = HotbarSlot.Shovel;
            mob.State |= PlayerStateFlags.Shooting;   // swings the shovel on every client's view
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

        private void StepSolver(ServerPlayer mob, Vector3 intent, float dt)
        {
            long started = Stopwatch.GetTimestamp();
            PlayerMovement.Step(terrain, ref mob.Move, intent, mob.State, dt);
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

            latestStats = FormattableString.Invariant(
                $"AI 1s avg: agents {agents:0.0}; movement {movementUsPerTick:0.0} us/tick (solver {solverUsPerTick:0.0} | combat {combatUsPerTick:0.0}, entrench {entrenchUsPerTick:0.0}, follow {movementUsPerTick - combatUsPerTick - entrenchUsPerTick:0.0} [headroom {headroomUs:0.0}, path {followerUs:0.0}]); perception {perceptionUsPerTick:0.0} us/tick; cover {coverUsPerTick:0.0} us/tick ({timingCoverQueries} queries); {navigation.WorkerCount} path workers {pathUsPerTick:0.0} aggregate us/tick off-thread; paths {requests} requested, {completed} completed ({complete} full/{partial} partial), queue {queueUsPerPath:0} us/path p50/p95 {queueP50}/{queueP95} us, search p50/p95 {searchP50}/{searchP95} us, {nodesPerPath:0} nodes/path, {metresPerPath:0.0} m/path, traversal cache {cacheHits} hits, shared routes {sharedReuses}, {cancelled} cancelled, {invalidated} spatially invalidated");

            timingTicks = 0;
            timingAgentSamples = 0;
            timingMovementStopwatchTicks = 0;
            timingSolverStopwatchTicks = 0;
            timingCombatStopwatchTicks = 0;
            timingEntrenchStopwatchTicks = 0;
            timingHeadroomStopwatchTicks = 0;
            timingFollowerStopwatchTicks = 0;
            timingPerceptionStopwatchTicks = 0;
            timingCoverStopwatchTicks = 0;
            timingCoverQueries = 0;
            timingNavigationQueueUs.Clear();
            timingNavigationSearchUs.Clear();
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
        private MobBrain CreateBrain(int team, Vector3 home)
        {
            int squadIndex = 0;
            _ = squads.TryAdd((team, squadIndex), new SquadBlackboard());
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
                return false;
            long? blockedCellKey = navigationAgent.TakeAvoidedCell();
            NavCell? preferredDigSite = navigationAgent.PreferredDigSite(tick);
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
                allowDig: allowDig,
                blockedCellKey: blockedCellKey,
                sharedRouteKey: sharedRouteKey,
                priority: forCover ? NavigationPriority.Combat : priority,
                preferredDigSite: preferredDigSite);
            if (requestId != 0)
                navigationAgent.RecordRequest(requestId, forCover);
            return requestId != 0;
        }

        private bool TryCellAt(
            Vector3 position,
            out NavCell cell,
            int horizontalRadius = 3)
            => NavTraversal.TryFindNearestStandable(
                terrain,
                position,
                horizontalRadius,
                out cell);

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
        /// Where one man walks on the way to his squad's objective — his slot in the wedge, not the
        /// objective itself.
        ///
        /// This used to be a golden-angle ring around the destination, which spread the squad only
        /// once it had ARRIVED: for the whole approach every man steered at the same point and they
        /// travelled as a clump, which is one grenade for the squad. The wedge is oriented on the
        /// approach, so it spreads them for the journey as well as the arrival.
        /// </summary>
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

            var slotPosition = WedgeFormation.Slot(centre, squad.Centre, slot);
            return SurfaceQuery.SurfacePosition(terrain, slotPosition.X, slotPosition.Z);
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

                brain.MarkUnderFire(tick);
                brain.NextCoverQueryTick = tick;
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
