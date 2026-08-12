namespace Demiurge.Tests;

public class PlayerStateFlagTests
{
    [Fact]
    public void SquadLeaderSurvivesTheLocalPredictionMergeOnlyWhenServerSetIt()
    {
        var local = PlayerStateFlags.Moving | PlayerStateFlags.SquadLeader;

        var leader = ServerAuthoredState.Merge(local, PlayerStateFlags.SquadLeader);
        var member = ServerAuthoredState.Merge(local, PlayerStateFlags.None);

        Assert.True(leader.HasFlag(PlayerStateFlags.Moving));
        Assert.True(leader.HasFlag(PlayerStateFlags.SquadLeader));
        Assert.True(member.HasFlag(PlayerStateFlags.Moving));
        Assert.False(member.HasFlag(PlayerStateFlags.SquadLeader));
    }
}
