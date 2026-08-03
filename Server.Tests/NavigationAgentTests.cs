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
}
