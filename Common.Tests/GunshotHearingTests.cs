using System.Numerics;

namespace Demiurge.Tests;

public class GunshotHearingTests
{
    [Fact]
    public void NearbyEnemyShotIsHeardButFriendlyOrDistantShotIsNot()
    {
        Vector3 listener = Vector3.Zero;

        Assert.True(GunshotHearing.CanHear(
            listenerTeam: 1,
            listener,
            shooterTeam: 2,
            shotPosition: new Vector3(GunshotHearing.MaximumDistance, 0f, 0f)));
        Assert.False(GunshotHearing.CanHear(
            listenerTeam: 1,
            listener,
            shooterTeam: 1,
            shotPosition: Vector3.One));
        Assert.False(GunshotHearing.CanHear(
            listenerTeam: 1,
            listener,
            shooterTeam: 2,
            shotPosition: new Vector3(GunshotHearing.MaximumDistance + 0.1f, 0f, 0f)));
    }
}
