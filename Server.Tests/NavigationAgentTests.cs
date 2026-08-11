using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public sealed class NavigationAgentTests
{
    [Fact]
    public void PrefetchIsLimitedToTwicePerSecond()
    {
        var agent = new NavigationAgent();

        Assert.True(agent.CanPrefetch(100));
        agent.RecordPrefetch(100);

        Assert.False(agent.CanPrefetch(100));
        Assert.False(agent.CanPrefetch(100 + NetworkConfig.TickRate / 2 - 1));
        Assert.True(agent.CanPrefetch(100 + NetworkConfig.TickRate / 2));
    }

    [Fact]
    public void ClearRemovesPrefetchDelay()
    {
        var agent = new NavigationAgent();
        agent.RecordPrefetch(100);

        agent.Clear();

        Assert.True(agent.CanPrefetch(100));
    }

    [Fact]
    public void RepeatedPartialPrefixMarksATrapEvenWhenItRepeatsTheSameCell()
    {
        var agent = new NavigationAgent();
        var start = new NavCell(125, 23, 239);

        for (int attempt = 0; attempt < 4; attempt++)
            agent.RememberPartialBacktrack(start);

        Assert.True(agent.IsRecoveringFromPartialTrap);
        Assert.Equal(4, agent.PartialBacktrackAttempts);
        Assert.Single(agent.PartialBacktrackCellKeys);
    }

    [Fact]
    public void SquadTransferCanPreserveTrapRecoveryButRespawnClearsIt()
    {
        var agent = new NavigationAgent();
        for (int attempt = 0; attempt < 4; attempt++)
            agent.RememberPartialBacktrack(new NavCell(125, 23, 239));

        agent.Clear(preservePartialBacktrack: true);
        Assert.True(agent.IsRecoveringFromPartialTrap);

        agent.Clear();
        Assert.False(agent.IsRecoveringFromPartialTrap);
        Assert.Empty(agent.PartialBacktrackCellKeys);
    }
}
