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
        private readonly ChunkMap terrain;
        private readonly NavigationSystem navigation;
        private readonly Perception perception;
        private readonly CombatBehavior combat;
        private readonly Random random;
        private readonly Dictionary<ushort, Vector3> destinations = new();
        private readonly Dictionary<ushort, Vector3> homes = new();
        private readonly Dictionary<ushort, MobBrain> brains = new();
        private readonly Dictionary<ushort, long> pendingRequests = new();
        private int timingTicks;
        private int timingAgentSamples;
        private long timingMovementStopwatchTicks;
        private long timingPerceptionStopwatchTicks;
        private NavigationSystem.Metrics timingNavigationStart;
        private string latestStats = "AI stats are collecting their first 1-second window";

        public MobSystem(ChunkMap terrain, WeaponSystem weapons, int seed = 0x51A7)
        {
            this.terrain = terrain;
            navigation = new NavigationSystem(terrain);
            perception = new Perception(terrain);
            combat = new CombatBehavior(weapons, terrain);
            random = new Random(seed);
        }

        public Vector3 RandomSpawnPoint() => RandomSurfacePoint(Vector3.Zero);

        public ServerPlayer CreateMob(ushort id, Vector3 position)
        {
            var mob = new ServerPlayer
            {
                Id = id,
                IsMob = true,
                Move = PlayerMovement.SpawnAt(terrain, position.X, position.Z),
            };
            homes[mob.Id] = mob.Position;
            destinations[mob.Id] = RandomSurfacePoint(mob.Position);
            brains[mob.Id] = new MobBrain();
            return mob;
        }

        public void BeginTick(uint tick, ICollection<ServerPlayer> actors)
        {
            while (navigation.TryGetCompleted(out var result))
            {
                if (!pendingRequests.TryGetValue(result.MobId, out long expected)
                    || expected != result.RequestId)
                    continue;

                pendingRequests.Remove(result.MobId);
                if (!brains.TryGetValue(result.MobId, out var brain))
                    continue;

                if (result.TerrainVersion == terrain.EditVersion
                    && result.Path.Waypoints.Count > 0)
                {
                    brain.Path.SetPath(result.Path, result.TerrainVersion);
                }
                else
                {
                    brain.Path.Clear();
                    destinations[result.MobId] = RandomSurfacePoint(
                        homes.GetValueOrDefault(result.MobId));
                }
            }

            long started = Stopwatch.GetTimestamp();
            foreach (var actor in actors)
                if (actor.IsMob && actor.Status is not { Health.Current: 0 }
                    && brains.TryGetValue(actor.Id, out var brain))
                    perception.Tick(actor, actors, brain, tick);
            timingPerceptionStopwatchTicks += Stopwatch.GetTimestamp() - started;
        }

        public void Step(ServerPlayer mob, float dt, uint tick)
        {
            if (!destinations.TryGetValue(mob.Id, out var destination))
                destination = destinations[mob.Id] = RandomSurfacePoint(HomeOf(mob));

            if (!brains.TryGetValue(mob.Id, out var brain))
                brains[mob.Id] = brain = new MobBrain();
            if (combat.Tick(mob, brain, tick, dt))
            {
                PlayerMovement.Step(
                    terrain,
                    ref mob.Move,
                    Vector3.Zero,
                    mob.State,
                    dt);
                return;
            }
            var follower = brain.Path;

            var followState = follower.Update(
                mob.Position,
                mob.Move.Grounded,
                terrain.EditVersion,
                out var intent,
                out bool jump);
            if (followState == PathFollowState.Complete)
            {
                if (follower.ReachedGoal)
                    destination = destinations[mob.Id] = RandomSurfacePoint(HomeOf(mob));
                follower.Clear();
                followState = PathFollowState.NeedsPath;
            }

            if (followState == PathFollowState.NeedsPath)
            {
                intent = Vector3.Zero;
                jump = false;
                RequestPath(mob, destination);
            }

            mob.State = PlayerStateFlags.None
                .With(PlayerStateFlags.Moving, intent != Vector3.Zero)
                .With(PlayerStateFlags.Jumping, jump);
            mob.Yaw = intent != Vector3.Zero ? MathF.Atan2(intent.X, intent.Z) : mob.Yaw;
            mob.Pitch = 0f;
            mob.LastIntent = intent;

            PlayerMovement.Step(terrain, ref mob.Move, intent, mob.State, dt);
        }

        public void Dispose() => navigation.Dispose();

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
            double pathUsPerTick = pathUs / (double)timingTicks;
            double agents = timingAgentSamples / (double)timingTicks;

            latestStats = FormattableString.Invariant(
                $"AI 1s avg: agents {agents:0.0}; movement {movementUsPerTick:0.0} us/tick; perception {perceptionUsPerTick:0.0} us/tick; path worker {pathUsPerTick:0.0} us/tick off-thread; paths {requests} requested, {completed} completed");

            timingTicks = 0;
            timingAgentSamples = 0;
            timingMovementStopwatchTicks = 0;
            timingPerceptionStopwatchTicks = 0;
        }

        public string Stats() => latestStats;

        private Vector3 HomeOf(ServerPlayer mob)
            => homes.TryGetValue(mob.Id, out var home) ? home : mob.Position;

        private void RequestPath(ServerPlayer mob, Vector3 destination)
        {
            if (pendingRequests.ContainsKey(mob.Id)) return;
            if (!TryCellAt(mob.Position, out var start)
                || !TryCellAt(destination, out var target))
                return;

            long requestId = navigation.Request(
                mob.Id,
                start,
                new GoalNear(target, ArriveDistance));
            if (requestId != 0)
                pendingRequests[mob.Id] = requestId;
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
    }
}
