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
                        ? ObjectiveDestination(mob.Id, objective.Position, squad)
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
            bool mayFire = !holdingForEffectiveRange
                && squad.TryAcquireEngagement(mob.Id, tick);
            if (holdingForEffectiveRange)
                squad.ReleaseEngagement(mob.Id);
            bool underFire =
                brain.IsUnderFire(tick)
                || mob.Spread.SuppressionMoa > 1f;
            if (combat.Tick(
                    mob,
                    brain,
                    tick,
                    dt,
                    mayFire && !underFire,
                    suppressing && mayFire))
            {
                brain.Navigation.Progress.Reset();
                Vector3 combatIntent;
                bool combatJump;
                if (bounding)
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
                    if (terrainProgress)
                        brain.Navigation.RememberDigSite(digTarget, tick);
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
                    tick,
                    forCover: false,
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

            PlayerMovement.Step(terrain, ref mob.Move, intent, mob.State, dt);
            if (brain.Navigation.Progress.Update(
                    mob.Position,
                    tick,
                    expectedToTravel: !holdingObjective,
                    terrainProgress))
                stuckMobs.Enqueue(mob.Id);
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
            brain.AssaultDashActive = false;
            brain.NextCoverQueryTick = 0;
            CancelPending(mob.Id, brain.Navigation);
            brain.Navigation.Clear();
            homes[mob.Id] = mob.Position;
            brain.Navigation.SetDestination(RandomSurfacePoint(mob.Position));
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
                    allowJump: true);
                return;
            }

            squad.RefreshClaim(mob.Id, tick);
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
                out _,
                out var blockedCell);
            brain.Navigation.RememberBlocked(blockedCell);
            if (followState == PathFollowState.Complete)
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
                RequestPath(mob, brain.CoverDestination, tick, forCover: true, allowJump: true);
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

            // PPSH carriers make their first cover instead of racing rifles to whatever natural
            // position the scorer finds. Once the foxhole is complete, the normal query recognizes
            // it as a fighting position and marks the unit set for the squad planner.
            if (assault
                && baseOfFire
                && !brain.AtCover
                && DigEmergencyCover(mob, primaryThreat.Position, tick))
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
                    // Digging is deliberately still off here. The cover follower below discards the
                    // dig target and has no Digging case, so a dig waypoint would stall the NPC on it
                    // forever. Executing one needs the shovel, pitch, and yaw that CombatBehavior has
                    // already claimed for aiming, which is a behaviour decision rather than plumbing.
                    RequestPath(
                        mob,
                        advance,
                        tick,
                        forCover: true,
                        replacePending: true,
                        allowJump: true);
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
                    tick,
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
                // Every cover request used to force this off, so an NPC in a firefight -- which is
                // exactly when it is in a trench -- could never dig, because combat owns movement and
                // the objective path that permits digging never runs. The caller decides now; short
                // reposition requests still leave it at its default of false.
                allowDig: allowDig,
                blockedCellKey: blockedCellKey,
                sharedRouteKey: sharedRouteKey,
                priority: forCover ? NavigationPriority.Combat : priority,
                preferredDigSite: navigationAgent.PreferredDigSite(tick));
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
