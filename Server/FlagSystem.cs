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
        public required Vector3 Position { get; init; }
        public int LastReplicatedBucket { get; set; }
    }

    private readonly ObjectReplication objects;
    private readonly List<Flag> flags = [];
    private readonly Dictionary<int, int> nextSpawnByTeam = [];

    public FlagSystem(ObjectReplication objects) => this.objects = objects;

    public ServerObject Spawn(Vector3 position)
    {
        var obj = objects.Spawn(
            ObjectType.Flag,
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
            Position = position,
            LastReplicatedBucket = ProgressBucket(obj.Team.Progress),
        });
        return obj;
    }

    public void Tick(float dt, IEnumerable<ServerPlayer> players)
    {
        if (!float.IsFinite(dt) || dt <= 0f) return;

        float radiusSq = FlagConfig.CaptureRadius * FlagConfig.CaptureRadius;
        foreach (var flag in flags)
        {
            int occupyingTeam = FlagConfig.NeutralTeam;
            int occupyingPlayers = 0;
            bool contested = false;
            foreach (var player in players)
            {
                if (player.Team <= 0
                    || player.Status is not { Health.Current: > 0 }
                    || Vector3.DistanceSquared(player.Position, flag.Position) > radiusSq)
                    continue;

                if (occupyingTeam == FlagConfig.NeutralTeam)
                    occupyingTeam = player.Team;

                if (player.Team == occupyingTeam)
                    occupyingPlayers++;
                else
                {
                    contested = true;
                    break;
                }
            }

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
        }
    }

    private static int ProgressBucket(float progress)
        => Math.Clamp(
            (int)MathF.Floor(progress * FlagConfig.ProgressReplicationBuckets),
            0,
            FlagConfig.ProgressReplicationBuckets);

    public bool TrySpawnPosition(int team, out Vector3 position)
    {
        var controlled = flags
            .Where(flag => flag.Object.Team.Value == team)
            .OrderBy(flag => flag.Object.NetworkId)
            .ToArray();
        if (controlled.Length == 0)
        {
            position = default;
            return false;
        }

        int index = nextSpawnByTeam.GetValueOrDefault(team);
        nextSpawnByTeam[team] = index == int.MaxValue ? 0 : index + 1;
        var centre = controlled[index % controlled.Length].Position;
        int sample = index / controlled.Length;
        const float goldenAngle = 2.39996323f;
        float angle = sample * goldenAngle;
        float radius = FlagConfig.SpawnRadius * MathF.Sqrt(((sample % 8) + 0.5f) / 8f);
        position = centre + new Vector3(
            MathF.Cos(angle) * radius,
            0f,
            MathF.Sin(angle) * radius);
        return true;
    }
}
