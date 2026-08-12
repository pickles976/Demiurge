using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Server-authoritative, Battlefield-style flag control. An uncontested attacker first drains an
/// owned flag to neutral, then captures it. Multiple teams in the radius pause all progress.
/// </summary>
public sealed class FlagSystem
{
    private sealed class Flag
    {
        public required ServerObject Object { get; init; }
        public Vector3 Position => Object.Transform.Position;
        public int LastReplicatedBucket { get; set; }
        public float GroundY { get; set; }
        public float VerticalVelocity { get; set; }

        /// <summary>
        /// Teams with a living player inside the capture radius, refreshed every tick. Kept as
        /// state rather than recomputed on demand because Tick already walks every player against
        /// every flag, and spawn selection has no player list to walk.
        /// </summary>
        public HashSet<int> Occupants { get; } = [];
    }

    private readonly ObjectReplication objects;
    private readonly ChunkMap? terrain;
    private readonly ActivityFeedSystem? activityFeed;
    private readonly List<Flag> flags = [];
    private readonly Dictionary<int, int> nextSpawnByTeam = [];

    public FlagSystem(
        ObjectReplication objects,
        ActivityFeedSystem? activityFeed = null)
    {
        this.objects = objects;
        this.activityFeed = activityFeed;
    }

    public FlagSystem(
        ObjectReplication objects,
        ChunkMap terrain,
        ActivityFeedSystem? activityFeed = null)
        : this(objects, activityFeed)
    {
        this.terrain = terrain;
    }

    private long observedTerrainVersion = long.MinValue;

    public ServerObject Spawn(Vector3 position)
    {
        // A flag may be spawned after the system has already observed this terrain revision.
        // Force the next tick to resolve its floor rather than inheriting the other flags' answer.
        observedTerrainVersion = long.MinValue;
        var obj = objects.Spawn(
            ObjectType.ConquestFlag,
            NetComponents.Transform | NetComponents.Team,
            position,
            flag => flag.Team = new TeamState
            {
                Value = FlagConfig.NeutralTeam,
                Progress = 0f,
            });
        flags.Add(new Flag
        {
            Object = obj,
            GroundY = position.Y,
            LastReplicatedBucket = ProgressBucket(obj.Team.Progress),
        });
        return obj;
    }

    public void Tick(float dt, IEnumerable<ServerPlayer> players)
    {
        if (!float.IsFinite(dt) || dt <= 0f) return;

        UpdateGravity(dt);

        float radiusSq = FlagConfig.CaptureRadius * FlagConfig.CaptureRadius;
        foreach (var flag in flags)
        {
            int occupyingTeam = FlagConfig.NeutralTeam;
            int occupyingPlayers = 0;
            flag.Occupants.Clear();
            foreach (var player in players)
            {
                if (player.Team <= 0
                    || player.Status is not { Health.Current: > 0 }
                    || Vector3.DistanceSquared(player.Position, flag.Position) > radiusSq)
                    continue;

                flag.Occupants.Add(player.Team);
                if (occupyingTeam == FlagConfig.NeutralTeam)
                    occupyingTeam = player.Team;
                if (player.Team == occupyingTeam)
                    occupyingPlayers++;
            }

            // The scan no longer stops at the first enemy, because spawn selection needs to know
            // WHO is standing here, not merely that somebody disagrees. Contest is unchanged: more
            // than one team present. occupyingPlayers stays a count of the first team seen, which is
            // all it ever was and is unused once contested.
            bool contested = flag.Occupants.Count > 1;

            // Empty flags retain partial progress. Two or more present teams are genuinely
            // contested and pause, matching Conquest's readable "hold the area" tug-of-war.
            if (contested || occupyingTeam == FlagConfig.NeutralTeam) continue;

            int team = occupyingTeam;
            float delta = dt
                * Math.Min(occupyingPlayers, FlagConfig.MaxCapturePlayers)
                / FlagConfig.CaptureSeconds;
            ref var state = ref flag.Object.Team;
            int oldOwner = state.Value;
            int oldCapturingTeam = state.CapturingTeam;

            if (state.Value == team)
            {
                // Returning defenders rebuild ownership that an attacker had partially drained.
                state.CapturingTeam = FlagConfig.NeutralTeam;
                state.Progress = MathF.Min(1f, state.Progress + delta);
            }
            else if (state.Value != FlagConfig.NeutralTeam)
            {
                // The first phase removes the current controller. Capture for the attacker begins
                // from zero on a following tick, making neutralization an observable state.
                state.CapturingTeam = team;
                state.Progress = MathF.Max(0f, state.Progress - delta);
                if (state.Progress <= 0f)
                {
                    state.Value = FlagConfig.NeutralTeam;
                    state.Progress = 0f;
                }
            }
            else if (state.CapturingTeam != FlagConfig.NeutralTeam
                     && state.CapturingTeam != team
                     && state.Progress > 0f)
            {
                // A different team must erase the previous team's partial neutral capture before
                // it can build its own, rather than inheriting that team's work.
                state.Progress = MathF.Max(0f, state.Progress - delta);
                if (state.Progress <= 0f)
                    state.CapturingTeam = team;
            }
            else
            {
                state.CapturingTeam = team;
                state.Progress = MathF.Min(1f, state.Progress + delta);
                if (state.Progress >= 1f)
                {
                    state.Value = team;
                    state.CapturingTeam = FlagConfig.NeutralTeam;
                    state.Progress = 1f;
                }
            }

            int bucket = ProgressBucket(state.Progress);
            if (state.Value != oldOwner
                || state.CapturingTeam != oldCapturingTeam
                || bucket != flag.LastReplicatedBucket)
            {
                flag.Object.Dirty |= NetComponents.Team;
                flag.LastReplicatedBucket = bucket;
            }
            if (oldOwner != state.Value)
            {
                if (state.Value == FlagConfig.NeutralTeam)
                    activityFeed?.ReportFlagNeutralized(team, flag.Position);
                else
                    activityFeed?.ReportFlagCaptured(state.Value, flag.Position);
            }
        }
    }

    private void UpdateGravity(float dt)
    {
        if (terrain is null) return;

        long version = terrain.EditVersion;
        if (version != observedTerrainVersion)
        {
            observedTerrainVersion = version;
            foreach (var flag in flags)
            {
                var position = flag.Position;
                var origin = position + Vector3.UnitY * 0.2f;
                float maxDistance = ChunkConstants.WorldMaxY - ChunkConstants.WorldMinY;
                float floor = TerrainRaycast.Cast(
                        terrain,
                        origin,
                        -Vector3.UnitY,
                        maxDistance)?.Point.Y
                    ?? ChunkConstants.WorldMinY + ChunkConstants.BedrockThickness;

                // Gravity never raises a pole when terrain is built into or under it.
                flag.GroundY = MathF.Min(position.Y, floor);
            }
        }

        foreach (var flag in flags)
        {
            var position = flag.Position;
            if (position.Y <= flag.GroundY + 0.001f)
            {
                flag.VerticalVelocity = 0f;
                continue;
            }

            flag.VerticalVelocity -= 9.81f * dt;
            float nextY = MathF.Max(flag.GroundY, position.Y + flag.VerticalVelocity * dt);
            if (nextY == position.Y) continue;

            flag.Object.Transform.Position = new Vector3(position.X, nextY, position.Z);
            flag.Object.Dirty |= NetComponents.Transform;
            if (nextY <= flag.GroundY + 0.001f)
                flag.VerticalVelocity = 0f;
        }
    }

    /// <summary>Every flag's position, in spawn order.</summary>
    internal IReadOnlyList<Vector3> Positions
        => flags.Select(flag => flag.Position).ToArray();

    /// <summary>
    /// How many flags each team currently controls. A flag being drained still counts for its
    /// owner until it actually goes neutral, which is what makes the ticket bleed follow ownership
    /// rather than momentary presence.
    /// </summary>
    public IReadOnlyDictionary<int, int> ControlledCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (var flag in flags)
        {
            int owner = flag.Object.Team.Value;
            if (owner == FlagConfig.NeutralTeam) continue;
            counts[owner] = counts.GetValueOrDefault(owner) + 1;
        }
        return counts;
    }

    /// <summary>
    /// Hands a flag to a team outright, as a completed capture would.
    ///
    /// Scenario setup, not gameplay: nothing in a running match awards a flag without going through
    /// the capture loop in <see cref="Tick"/>. It exists so a benchmark can start from a mid-match
    /// position — home flags held, centre contested — instead of spending the first two minutes
    /// measuring thirty-two NPCs walking away from their own spawns.
    /// </summary>
    internal bool TryForceOwner(Vector3 position, int team)
    {
        var flag = flags.MinBy(candidate =>
            Vector3.DistanceSquared(candidate.Position, position));
        if (flag is null) return false;

        ref var state = ref flag.Object.Team;
        state.Value = team;
        state.CapturingTeam = FlagConfig.NeutralTeam;
        state.Progress = 1f;
        flag.Object.Dirty |= NetComponents.Team;
        flag.LastReplicatedBucket = ProgressBucket(state.Progress);
        return true;
    }

    private static int ProgressBucket(float progress)
        => Math.Clamp(
            (int)MathF.Floor(progress * FlagConfig.ProgressReplicationBuckets),
            0,
            FlagConfig.ProgressReplicationBuckets);

    /// <summary>
    /// Builds one small team-relative strategic snapshot. Called once per team per commander
    /// second, not per NPC tick, so the straightforward flag × actor scan remains both clearer and
    /// cheaper than maintaining another mutable occupancy index.
    /// </summary>
    internal IReadOnlyList<StrategicFlag> StrategicSnapshot(
        int team,
        ICollection<ServerPlayer> players)
    {
        float radiusSq = FlagConfig.CaptureRadius * FlagConfig.CaptureRadius;
        float influenceSq = StrategicValue.InfluenceRadius * StrategicValue.InfluenceRadius;
        var snapshot = new StrategicFlag[flags.Count];
        for (int i = 0; i < flags.Count; i++)
        {
            var flag = flags[i];
            int friendlyPresence = 0;
            int enemyPresence = 0;
            int friendlyApproaching = 0;
            float nearestEnemySquared = float.PositiveInfinity;
            foreach (var player in players)
            {
                if (player.Team <= 0 || player.Status is not { Health.Current: > 0 })
                    continue;

                float distanceSquared = Vector3.DistanceSquared(player.Position, flag.Position);
                if (player.Team == team)
                {
                    if (distanceSquared <= radiusSq) friendlyPresence++;
                    if (distanceSquared <= influenceSq) friendlyApproaching++;
                }
                else
                {
                    if (distanceSquared <= radiusSq) enemyPresence++;
                    nearestEnemySquared = MathF.Min(nearestEnemySquared, distanceSquared);
                }
            }

            snapshot[i] = new StrategicFlag(
                flag.Object.NetworkId,
                flag.Position,
                flag.Object.Team.Value,
                flag.Object.Team.CapturingTeam,
                flag.Object.Team.Progress,
                friendlyPresence,
                enemyPresence,
                friendlyApproaching,
                // Straight-line walk time. The planner is comparing flags against each other, and a
                // route's detours cost every candidate about the same; paying for a real path per
                // flag per team per second to learn that would be the expensive way to change no
                // decision.
                float.IsPositiveInfinity(nearestEnemySquared)
                    ? float.PositiveInfinity
                    : MathF.Sqrt(nearestEnemySquared) / PlayerMovement.WalkSpeed);
        }

        return snapshot;
    }

    /// <summary>
    /// Finds the closest flag this team can make useful progress on. Fully secured friendly flags
    /// are skipped; a friendly flag whose ownership is being drained remains a valid defensive
    /// objective until its progress is restored.
    /// </summary>
    internal bool TryGetSquadObjective(
        int team,
        Vector3 squadHome,
        uint currentFlagId,
        out SquadObjective objective)
    {
        // A squad that took a flag owns its local defence until explicitly reassigned by a later
        // objective layer. Keeping the same objective also makes it immediately retake the point
        // if an enemy neutralizes it.
        if (currentFlagId != 0)
        {
            foreach (var flag in flags)
            {
                if (flag.Object.NetworkId != currentFlagId) continue;
                objective = new SquadObjective(flag.Object.NetworkId, flag.Position);
                return true;
            }
        }

        Flag? nearest = null;
        float nearestDistance = float.PositiveInfinity;
        foreach (var flag in flags)
        {
            ref var state = ref flag.Object.Team;
            if (state.Value == team && state.Progress >= 1f)
                continue;

            float distance = Vector3.DistanceSquared(squadHome, flag.Position);
            if (distance > nearestDistance
                || (distance == nearestDistance
                    && nearest is not null
                    && flag.Object.NetworkId > nearest.Object.NetworkId))
                continue;

            nearest = flag;
            nearestDistance = distance;
        }

        if (nearest is null)
        {
            objective = default;
            return false;
        }

        objective = new SquadObjective(nearest.Object.NetworkId, nearest.Position);
        return true;
    }

    /// <summary>An enemy of <paramref name="team"/> is inside the capture radius.</summary>
    private static bool IsContestedFor(Flag flag, int team)
    {
        foreach (int occupant in flag.Occupants)
            if (occupant != team) return true;
        return false;
    }

    public bool TrySpawnPosition(int team, out Vector3 position)
    {
        // Spawn at the FRONT, not at the back. Ordering controlled flags by network id meant a
        // reinforcement was as likely to appear at the flag furthest from the fighting as the
        // nearest one, and then had to walk the length of the map to matter — which for an attacking
        // team is most of the round spent in transit.
        //
        // "Front" is defined by what is still to be taken: for each flag this team holds, how far it
        // is from the nearest flag this team does NOT hold. Ties and the no-objectives-left case
        // fall back on network id, so the ordering stays deterministic.
        var objectives = flags
            .Where(flag => flag.Object.Team.Value != team)
            .ToArray();

        // Never into a firefight. A flag with an enemy standing on it is either being taken or
        // about to be, and dropping reinforcements onto it one at a time feeds them in piecemeal —
        // the fallback in SpawnPlayerMove puts them at the team's authored spawns instead, which is
        // further back but somewhere they arrive alive.
        var controlled = flags
            .Where(flag => flag.Object.Team.Value == team && !IsContestedFor(flag, team))
            .OrderBy(flag => objectives.Length == 0
                ? 0f
                : objectives.Min(objective =>
                    Vector3.DistanceSquared(flag.Position, objective.Position)))
            .ThenBy(flag => flag.Object.NetworkId)
            .ToArray();
        if (controlled.Length == 0)
        {
            position = default;
            return false;
        }

        // The front flag, every time, rather than a round robin over everything held. Cycling was
        // the actual defect: sorting alone only changes which flag the cycle STARTS at, so
        // reinforcements still went to the rear ones in turn. The counter now only scatters men
        // within the one spawn circle so they do not stack on the flag itself.
        int index = nextSpawnByTeam.GetValueOrDefault(team);
        nextSpawnByTeam[team] = index == int.MaxValue ? 0 : index + 1;
        var centre = controlled[0].Position;
        const float goldenAngle = 2.39996323f;
        float angle = index * goldenAngle;
        float radius = FlagConfig.SpawnRadius * MathF.Sqrt(((index % 8) + 0.5f) / 8f);
        position = centre + new Vector3(
            MathF.Cos(angle) * radius,
            0f,
            MathF.Sin(angle) * radius);
        return true;
    }
}
