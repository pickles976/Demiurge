using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class NavigationProgressWatchTests
{
    [Fact]
    public void StationaryNavigationIsStuckAfterSixtySeconds()
    {
        var watch = new NavigationProgressWatch();

        Assert.False(watch.Update(Vector3.Zero, tick: 10, expectedToTravel: true));
        Assert.False(watch.Update(
            Vector3.Zero,
            tick: 10 + NavigationProgressWatch.StuckTicks - 1,
            expectedToTravel: true));
        Assert.True(watch.Update(
            Vector3.Zero,
            tick: 10 + NavigationProgressWatch.StuckTicks,
            expectedToTravel: true));
    }

    [Fact]
    public void MovementTerrainWorkAndDeliberateHoldingResetTheWatch()
    {
        var watch = new NavigationProgressWatch();
        Assert.False(watch.Update(Vector3.Zero, tick: 1, expectedToTravel: true));
        Assert.False(watch.Update(
            new Vector3(2f, 0f, 0f),
            tick: NavigationProgressWatch.StuckTicks,
            expectedToTravel: true));
        Assert.False(watch.Update(
            new Vector3(2f, 0f, 0f),
            tick: NavigationProgressWatch.StuckTicks * 2 - 1,
            expectedToTravel: true,
            terrainProgress: true));
        Assert.False(watch.Update(
            new Vector3(2f, 0f, 0f),
            tick: NavigationProgressWatch.StuckTicks * 3 - 2,
            expectedToTravel: false));
    }
}
