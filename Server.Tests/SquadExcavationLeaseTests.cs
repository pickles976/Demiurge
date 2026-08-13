using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public sealed class SquadExcavationLeaseTests
{
    [Fact]
    public void SquadmateReusesANearbyCommittedCut()
    {
        var board = new SquadBlackboard();
        var cut = new Vector3(5f, 8f, 7f);
        board.LeaseExcavation(ownerId: 7, cut, tick: 10);

        Assert.True(board.IsExcavationLeasedByOther(
            actorId: 9,
            new Vector3(9f, 8f, 7f),
            tick: 11));
        Assert.False(board.IsExcavationLeasedByOther(
            actorId: 7,
            new Vector3(9f, 8f, 7f),
            tick: 11));
    }

    [Fact]
    public void ExpiredOrDistantCutsDoNotRedirectAnActor()
    {
        var board = new SquadBlackboard();
        board.LeaseExcavation(ownerId: 7, Vector3.Zero, tick: 0);

        Assert.False(board.IsExcavationLeasedByOther(
            actorId: 9,
            new Vector3(30f, 0f, 0f),
            tick: 1));
        Assert.False(board.IsExcavationLeasedByOther(
            actorId: 9,
            Vector3.Zero,
            tick: 20u * NetworkConfig.TickRate + 1));
    }
}
