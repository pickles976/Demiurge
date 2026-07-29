using System.Numerics;
using Demiurge.GameServer;
using Riptide;

namespace Demiurge.ServerTests;

public class FlagSystemTests
{
    [Fact]
    public void FlagStartsNeutralCapturesOnTimerAndBecomesTeamSpawn()
    {
        var objects = new ObjectReplication(new Server());
        var flags = new FlagSystem(objects);
        var position = new Vector3(5f, 2f, 7f);
        var flag = flags.Spawn(position);
        var teamTwo = PlayerAt(1, team: 2, position);

        Assert.Equal(FlagConfig.NeutralTeam, flag.Team.Value);
        Assert.False(flags.TrySpawnPosition(2, out _));

        flags.Tick(FlagConfig.CaptureSeconds / 2f, [teamTwo]);

        Assert.Equal(FlagConfig.NeutralTeam, flag.Team.Value);
        Assert.Equal(2, flag.Team.CapturingTeam);
        Assert.Equal(0.5f, flag.Team.Progress, 4);
        Assert.False(flags.TrySpawnPosition(2, out _));

        flags.Tick(FlagConfig.CaptureSeconds / 2f, [teamTwo]);

        Assert.Equal(2, flag.Team.Value);
        Assert.Equal(FlagConfig.NeutralTeam, flag.Team.CapturingTeam);
        Assert.Equal(1f, flag.Team.Progress);
        Assert.True(flag.Dirty.HasFlag(NetComponents.Team));
        Assert.True(flags.TrySpawnPosition(2, out var spawn));
        Assert.InRange(
            Vector2.Distance(
                new Vector2(position.X, position.Z),
                new Vector2(spawn.X, spawn.Z)),
            0f,
            FlagConfig.SpawnRadius);
        Assert.Equal(position.Y, spawn.Y);
        Assert.False(flags.TrySpawnPosition(1, out _));
    }

    [Fact]
    public void ContestedFlagPausesCaptureProgress()
    {
        var objects = new ObjectReplication(new Server());
        var flags = new FlagSystem(objects);
        var flag = flags.Spawn(Vector3.Zero);
        var teamOne = PlayerAt(1, 1, new Vector3(1f, 0f, 0f));
        var teamTwo = PlayerAt(2, 2, new Vector3(-1f, 0f, 0f));

        flags.Tick(FlagConfig.CaptureSeconds / 2f, [teamOne]);
        Assert.Equal(FlagConfig.NeutralTeam, flag.Team.Value);
        Assert.Equal(0.5f, flag.Team.Progress, 4);
        flag.Dirty = NetComponents.None;

        flags.Tick(FlagConfig.CaptureSeconds, [teamOne, teamTwo]);

        Assert.Equal(FlagConfig.NeutralTeam, flag.Team.Value);
        Assert.Equal(1, flag.Team.CapturingTeam);
        Assert.Equal(0.5f, flag.Team.Progress, 4);
        Assert.Equal(NetComponents.None, flag.Dirty);
    }

    [Fact]
    public void EnemyMustNeutralizeOwnedFlagBeforeCapturingIt()
    {
        var objects = new ObjectReplication(new Server());
        var flags = new FlagSystem(objects);
        var flag = flags.Spawn(Vector3.Zero);
        var teamOne = PlayerAt(1, 1, Vector3.Zero);
        var teamTwo = PlayerAt(2, 2, Vector3.Zero);

        flags.Tick(FlagConfig.CaptureSeconds, [teamOne]);
        Assert.Equal(1, flag.Team.Value);

        flags.Tick(FlagConfig.CaptureSeconds / 2f, [teamTwo]);
        Assert.Equal(1, flag.Team.Value);
        Assert.Equal(2, flag.Team.CapturingTeam);
        Assert.Equal(0.5f, flag.Team.Progress, 4);
        Assert.True(flags.TrySpawnPosition(1, out _));

        flags.Tick(FlagConfig.CaptureSeconds / 2f, [teamTwo]);
        Assert.Equal(FlagConfig.NeutralTeam, flag.Team.Value);
        Assert.Equal(2, flag.Team.CapturingTeam);
        Assert.Equal(0f, flag.Team.Progress);
        Assert.False(flags.TrySpawnPosition(1, out _));
        Assert.False(flags.TrySpawnPosition(2, out _));

        flags.Tick(FlagConfig.CaptureSeconds, [teamTwo]);
        Assert.Equal(2, flag.Team.Value);
        Assert.Equal(1f, flag.Team.Progress);
        Assert.True(flags.TrySpawnPosition(2, out _));
    }

    [Fact]
    public void AdditionalTeammatesAccelerateCaptureUpToConfiguredCap()
    {
        var objects = new ObjectReplication(new Server());
        var flags = new FlagSystem(objects);
        var flag = flags.Spawn(Vector3.Zero);
        var first = PlayerAt(1, 3, Vector3.Zero);
        var second = PlayerAt(2, 3, Vector3.UnitX);

        flags.Tick(FlagConfig.CaptureSeconds / 2f, [first, second]);

        Assert.Equal(3, flag.Team.Value);
        Assert.Equal(1f, flag.Team.Progress);
    }

    [Fact]
    public void SquadObjectiveChoosesNearestFlagThatIsNotSecurelyFriendly()
    {
        var objects = new ObjectReplication(new Server());
        var flags = new FlagSystem(objects);
        var nearPosition = new Vector3(5f, 0f, 0f);
        var farPosition = new Vector3(25f, 0f, 0f);
        var near = flags.Spawn(nearPosition);
        var far = flags.Spawn(farPosition);

        Assert.True(flags.TryGetSquadObjective(
            1,
            Vector3.Zero,
            currentFlagId: 0,
            out var neutral));
        Assert.Equal(near.NetworkId, neutral.FlagId);

        flags.Tick(
            FlagConfig.CaptureSeconds,
            [PlayerAt(1, team: 1, nearPosition)]);

        Assert.True(flags.TryGetSquadObjective(
            1,
            Vector3.Zero,
            currentFlagId: 0,
            out var teamOne));
        Assert.Equal(far.NetworkId, teamOne.FlagId);
        Assert.True(flags.TryGetSquadObjective(
            1,
            Vector3.Zero,
            currentFlagId: near.NetworkId,
            out var defendingTeamOne));
        Assert.Equal(near.NetworkId, defendingTeamOne.FlagId);
        Assert.True(flags.TryGetSquadObjective(
            2,
            Vector3.Zero,
            currentFlagId: 0,
            out var teamTwo));
        Assert.Equal(near.NetworkId, teamTwo.FlagId);
    }

    private static ServerPlayer PlayerAt(ushort id, int team, Vector3 position)
        => new()
        {
            Id = id,
            Team = team,
            Move = new MoveState { Position = position },
            Status = new ServerObject
            {
                Has = NetComponents.Health,
                Health = new HealthState { Current = 100, Max = 100 },
            },
        };
}
