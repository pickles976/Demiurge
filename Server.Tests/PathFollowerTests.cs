using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class PathFollowerTests
{
    [Fact]
    public void JumpKeepsPlannedIntentUntilTheActorLands()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 0, 0), Vector3.Zero),
                    new NavWaypoint(
                        new NavCell(1, 0, 0),
                        Vector3.UnitX,
                        NavAction.Jump),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 7);

        Assert.Equal(
            PathFollowState.Following,
            follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 7,
                out var takeoff,
                out bool jump));
        Assert.True(jump);
        Assert.Equal(Vector3.UnitX, takeoff);

        Assert.Equal(
            PathFollowState.Following,
            follower.Update(
                new Vector3(1.2f, 0.8f, 0f),
                grounded: false,
                currentTerrainVersion: 7,
                out var airborne,
                out jump));
        Assert.False(jump);
        Assert.Equal(Vector3.UnitX, airborne);

        Assert.Equal(
            PathFollowState.Complete,
            follower.Update(
                new Vector3(1.3f, 0f, 0f),
                grounded: true,
                currentTerrainVersion: 7,
                out _,
                out _));
    }
}
