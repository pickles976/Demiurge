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
                Vector3.Zero,
                out var takeoff,
                out bool jump,
                out _,
                out _));
        Assert.True(jump);
        Assert.Equal(Vector3.UnitX, takeoff);

        Assert.Equal(
            PathFollowState.Following,
            follower.Update(
                new Vector3(1.2f, 0.8f, 0f),
                grounded: false,
                currentTerrainVersion: 7,
                Vector3.Zero,
                out var airborne,
                out jump,
                out _,
                out _));
        Assert.False(jump);
        Assert.Equal(Vector3.UnitX, airborne);

        Assert.Equal(
            PathFollowState.Complete,
            follower.Update(
                new Vector3(1.3f, 0f, 0f),
                grounded: true,
                currentTerrainVersion: 7,
                Vector3.Zero,
                out _,
                out _,
                out _,
                out _));
    }

    [Fact]
    public void DigWaypointStopsAtTheFrontierAndReportsItsVoxel()
    {
        var follower = new PathFollower();
        // Inside the ordinary arrival radius: dig actions must not be consumed as movement
        // waypoints just because the brush target is close to the actor.
        var target = new Vector3(0.4f, 13f, 0f);
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(
                        new NavCell(1, 12, 0),
                        target,
                        NavAction.Dig),
                ],
                ReachedGoal: false,
                Cost: NavCosts.DigOneVoxel,
                ExpandedNodes: 1),
            version: 4);

        Assert.Equal(
            PathFollowState.Digging,
            follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 4,
                Vector3.Zero,
                out var intent,
                out bool jump,
                out var digTarget,
                out _));
        Assert.Equal(Vector3.Zero, intent);
        Assert.False(jump);
        Assert.Equal(target, digTarget);
    }

    [Fact]
    public void TerrainEditLetsEveryAlreadyPlannedDiggerContributeOneBite()
    {
        var follower = new PathFollower();
        var target = new Vector3(1f, 13f, 0f);
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(new NavCell(1, 12, 0), target, NavAction.Dig),
                ],
                ReachedGoal: false,
                Cost: NavCosts.DigOneVoxel,
                ExpandedNodes: 1),
            version: 4);

        Assert.Equal(
            PathFollowState.Digging,
            follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 5,
                Vector3.Zero,
                out _,
                out _,
                out var digTarget,
                out _));
        Assert.Equal(target, digTarget);

        Assert.Equal(
            PathFollowState.NeedsPath,
            follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 5,
                Vector3.Zero,
                out _,
                out _,
                out _,
                out _));
    }

    [Fact]
    public void PlannedJumpThatNeverTakesOffReplansInsteadOfHoppingForever()
    {
        var follower = new PathFollower();
        var blocked = new NavCell(1, 13, 0);
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(blocked, Vector3.UnitX, NavAction.Jump),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 2);

        NavCell? reported = null;
        PathFollowState state = PathFollowState.Following;
        for (int tick = 0; tick < NetworkConfig.TickRate && reported is null; tick++)
            state = follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 2,
                Vector3.Zero,
                out _,
                out _,
                out _,
                out reported);

        Assert.Equal(PathFollowState.NeedsPath, state);
        Assert.Equal(blocked, reported);
    }

    [Fact]
    public void PlannedJumpThatLandsBackAtTheWallReplans()
    {
        var follower = new PathFollower();
        var blocked = new NavCell(2, 13, 0);
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(blocked, new Vector3(2f, 1f, 0f), NavAction.Jump),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 2);

        _ = follower.Update(
            Vector3.Zero,
            grounded: true,
            currentTerrainVersion: 2,
            Vector3.Zero,
            out _,
            out _,
            out _,
            out _);
        _ = follower.Update(
            new Vector3(0.4f, 0.8f, 0f),
            grounded: false,
            currentTerrainVersion: 2,
            Vector3.Zero,
            out _,
            out _,
            out _,
            out _);

        Assert.Equal(
            PathFollowState.NeedsPath,
            follower.Update(
                new Vector3(0.4f, 0f, 0f),
                grounded: true,
                currentTerrainVersion: 2,
                Vector3.Zero,
                out _,
                out _,
                out _,
                out var reported));
        Assert.Equal(blocked, reported);
    }

    [Fact]
    public void RepeatedMovementStallReportsTheBlockedWaypointForReplanning()
    {
        var follower = new PathFollower();
        var blocked = new NavCell(1, 12, 0);
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(blocked, Vector3.UnitX),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 2);

        NavCell? reported = null;
        PathFollowState state = PathFollowState.Following;
        for (int tick = 0; tick < NetworkConfig.TickRate && reported is null; tick++)
            state = follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 2,
                Vector3.Zero,
                out _,
                out _,
                out _,
                out reported);

        Assert.Equal(PathFollowState.NeedsPath, state);
        Assert.Equal(blocked, reported);
    }

    [Fact]
    public void StalledUphillWalkJumpsBeforeDiscardingThePath()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(
                        new NavCell(1, 13, 0),
                        new Vector3(1f, 0.5f, 0f)),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 2);

        bool jumped = false;
        PathFollowState state = PathFollowState.Following;
        for (int tick = 0; tick < NetworkConfig.TickRate && !jumped; tick++)
        {
            state = follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 2,
                Vector3.Zero,
                out var intent,
                out jumped,
                out _,
                out _);
            Assert.True(intent.X > 0.99f);
        }

        Assert.True(jumped);
        Assert.Equal(PathFollowState.Following, state);
    }

    [Fact]
    public void StalledOneMetreStaircaseTreadJumpsBeforeDiscardingThePath()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(
                        new NavCell(1, 13, 0),
                        new Vector3(1f, 1f, 0f)),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 2);

        bool jumped = false;
        for (int tick = 0; tick < NetworkConfig.TickRate && !jumped; tick++)
            _ = follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 2,
                Vector3.Zero,
                out _,
                out jumped,
                out _,
                out _);

        Assert.True(jumped);
    }

    [Fact]
    public void StalledLevelWalkReplansWithoutJumping()
    {
        var follower = new PathFollower();
        var blocked = new NavCell(1, 12, 0);
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(blocked, Vector3.UnitX),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 2);

        bool everJumped = false;
        NavCell? reported = null;
        PathFollowState state = PathFollowState.Following;
        for (int tick = 0; tick < NetworkConfig.TickRate && reported is null; tick++)
        {
            state = follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 2,
                Vector3.Zero,
                out _,
                out bool jump,
                out _,
                out reported);
            everJumped |= jump;
        }

        Assert.False(everJumped);
        Assert.Equal(PathFollowState.NeedsPath, state);
        Assert.Equal(blocked, reported);
    }

    [Fact]
    public void FailedUphillRecoveryEventuallyReplans()
    {
        var follower = new PathFollower();
        var blocked = new NavCell(1, 13, 0);
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), Vector3.Zero),
                    new NavWaypoint(blocked, new Vector3(1f, 0.5f, 0f)),
                ],
                ReachedGoal: true,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 2);

        int jumps = 0;
        NavCell? reported = null;
        PathFollowState state = PathFollowState.Following;
        for (int tick = 0; tick < NetworkConfig.TickRate * 2 && reported is null; tick++)
        {
            state = follower.Update(
                Vector3.Zero,
                grounded: true,
                currentTerrainVersion: 2,
                Vector3.Zero,
                out _,
                out bool jump,
                out _,
                out reported);
            if (jump) jumps++;
        }

        Assert.Equal(1, jumps);
        Assert.Equal(PathFollowState.NeedsPath, state);
        Assert.Equal(blocked, reported);
    }

    [Fact]
    public void ReplacementPartialPathJoinsAheadOfTheMovingActor()
    {
        var follower = new PathFollower();
        var waypoints = Enumerable.Range(0, 12)
            .Select(x => new NavWaypoint(
                new NavCell(x, 12, 0),
                new Vector3(x, 12f, 0f)))
            .ToArray();
        follower.SetPath(
            new NavPath(
                waypoints,
                ReachedGoal: false,
                Cost: 3f,
                ExpandedNodes: 20),
            version: 3,
            currentPosition: new Vector3(5.2f, 12f, 0f));

        Assert.Equal(
            PathFollowState.Following,
            follower.Update(
                new Vector3(5.2f, 12f, 0f),
                grounded: true,
                currentTerrainVersion: 3,
                Vector3.Zero,
                out var intent,
                out _,
                out _,
                out _));
        Assert.True(intent.X > 0.99f);
        Assert.True(follower.ShouldRefreshPath);
    }

    [Fact]
    public void ReplacementPathDoesNotSkipAnUnreachedBridgeEntrance()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(-2, 12, 0), new Vector3(-2f, 12f, 0f)),
                    new NavWaypoint(new NavCell(0, 12, 0), new Vector3(0f, 12f, 0f)),
                    new NavWaypoint(new NavCell(0, 12, 1), new Vector3(0f, 12f, 1f)),
                ],
                ReachedGoal: false,
                Cost: 1f,
                ExpandedNodes: 3),
            version: 3,
            currentPosition: new Vector3(-0.6f, 12f, 0f));

        Assert.Equal(
            PathFollowState.Following,
            follower.Update(
                new Vector3(-0.6f, 12f, 0f),
                grounded: true,
                currentTerrainVersion: 3,
                Vector3.Zero,
                out var intent,
                out _,
                out _,
                out _));
        Assert.True(intent.X > 0.99f);
        Assert.InRange(MathF.Abs(intent.Z), 0f, 0.01f);
    }

    [Fact]
    public void ReplacementPathCannotJoinTheFarBankAcrossABridgeDetour()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), new Vector3(0f, 12f, 0f)),
                    new NavWaypoint(new NavCell(2, 12, 0), new Vector3(2f, 12f, 0f)),
                    new NavWaypoint(new NavCell(2, 12, 1), new Vector3(2f, 12f, 1f)),
                    new NavWaypoint(new NavCell(0, 12, 1), new Vector3(0f, 12f, 1f)),
                ],
                ReachedGoal: false,
                Cost: 6f,
                ExpandedNodes: 40),
            version: 3,
            // The far-bank waypoint is only one metre away in world space, but reaching it along
            // this route requires walking the full bridge detour.
            currentPosition: new Vector3(0f, 12f, 0.8f));

        Assert.Equal(
            PathFollowState.Following,
            follower.Update(
                new Vector3(0f, 12f, 0.8f),
                grounded: true,
                currentTerrainVersion: 3,
                Vector3.Zero,
                out var intent,
                out _,
                out _,
                out _));
        Assert.True(intent.Z < -0.99f, $"joined across the ditch with intent {intent}");
    }

    [Fact]
    public void PlannedBridgeJumpCentersOnItsTakeoffBeforeLaunching()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 12, 0), new Vector3(0.5f, 12f, 0.5f)),
                    new NavWaypoint(
                        new NavCell(0, 12, 3),
                        new Vector3(0.5f, 12f, 3.5f),
                        NavAction.Jump),
                ],
                ReachedGoal: false,
                Cost: 1f,
                ExpandedNodes: 2),
            version: 3);

        Assert.Equal(
            PathFollowState.Following,
            follower.Update(
                new Vector3(0.85f, 12f, 0.5f),
                grounded: true,
                currentTerrainVersion: 3,
                Vector3.Zero,
                out var intent,
                out bool jump,
                out _,
                out _));
        Assert.False(jump);
        Assert.True(intent.X < -0.99f, $"launched off-centre with intent {intent}");
        Assert.InRange(MathF.Abs(intent.Z), 0f, 0.01f);
    }

    [Fact]
    public void PlannedJumpCannotBeReplacedOrPrefetchedInFlight()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(
                        new NavCell(0, 12, 3),
                        new Vector3(0.5f, 12f, 3.5f),
                        NavAction.Jump),
                ],
                ReachedGoal: false,
                Cost: 1f,
                ExpandedNodes: 1),
            version: 3);

        _ = follower.Update(
            new Vector3(0.5f, 12f, 0.5f),
            grounded: true,
            currentTerrainVersion: 3,
            Vector3.Zero,
            out _,
            out bool jump,
            out _,
            out _);

        Assert.True(jump);
        Assert.False(follower.CanReplacePath);
        Assert.False(follower.ShouldRefreshPath);
    }

    /// <summary>
    /// Formation is a property of the march, not of the destination. Two men following one route
    /// with opposite lanes must be steered apart WHILE they walk — the wedge used to exist only at
    /// the objective, so a squad crossing open ground filed along one line the whole way and only
    /// spread out on arrival.
    /// </summary>
    [Fact]
    public void OppositeLanesSteerFollowersApartAlongTheSameRoute()
    {
        var left = LongRouteFollower();
        var right = LongRouteFollower();

        left.Update(
            Vector3.Zero, grounded: true, currentTerrainVersion: 7,
            lateral: new Vector3(-1f, 0f, 0f),
            out var leftIntent, out _, out _, out _);
        right.Update(
            Vector3.Zero, grounded: true, currentTerrainVersion: 7,
            lateral: new Vector3(1f, 0f, 0f),
            out var rightIntent, out _, out _, out _);

        Assert.True(leftIntent.X < 0f, $"left man should be steered left, got {leftIntent}");
        Assert.True(rightIntent.X > 0f, $"right man should be steered right, got {rightIntent}");

        // And still going where the route goes: a lane, not a detour.
        Assert.True(leftIntent.Z > 0.5f, $"forward progress dominates, got {leftIntent}");
        Assert.True(rightIntent.Z > 0.5f, $"forward progress dominates, got {rightIntent}");
    }

    /// <summary>
    /// The lane closes as the waypoint nears, or a man crabs straight past the point he was supposed
    /// to arrive at and the follower never advances.
    /// </summary>
    [Fact]
    public void TheLaneClosesOnApproachSoTheWaypointIsStillReached()
    {
        var follower = LongRouteFollower();
        var lane = new Vector3(1f, 0f, 0f);

        follower.Update(
            Vector3.Zero, grounded: true, currentTerrainVersion: 7, lane,
            out var far, out _, out _, out _);
        follower.Update(
            new Vector3(0f, 0f, 39f), grounded: true, currentTerrainVersion: 7, lane,
            out var near, out _, out _, out _);

        Assert.True(
            MathF.Abs(near.X) < MathF.Abs(far.X),
            $"lane {near.X} at the waypoint should be tighter than {far.X} out on the leg");
    }

    private static PathFollower LongRouteFollower()
    {
        var follower = new PathFollower();
        follower.SetPath(
            new NavPath(
                [
                    new NavWaypoint(new NavCell(0, 0, 0), Vector3.Zero),
                    new NavWaypoint(new NavCell(0, 0, 40), new Vector3(0f, 0f, 40f)),
                ],
                ReachedGoal: true,
                Cost: 40f,
                ExpandedNodes: 2),
            version: 7);
        return follower;
    }
}
