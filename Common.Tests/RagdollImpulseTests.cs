using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the death-shove model, not a trace through it. What matters is that the direction
/// says what killed you, that harder blows throw you further, and that nothing degenerate reaches a
/// corpse — the constants themselves are a look-and-feel question and deliberately not asserted.
/// </summary>
public class RagdollImpulseTests
{
    [Fact]
    public void BulletShovesAlongTheRoundsTravel()
    {
        var impulse = RagdollImpulse.FromBullet(new Vector3(0f, 0f, 900f), damage: 30);

        Assert.True(impulse.Z > 0f);
        Assert.Equal(0f, impulse.X, 5);
        Assert.Equal(0f, impulse.Y, 5);
    }

    [Fact]
    public void SpeedIsIndependentOfHowFastTheRoundWasGoing()
    {
        // The direction is taken from the projectile's velocity, so a fast round must not shove
        // harder than a slow one carrying the same damage — only damage may do that.
        var slow = RagdollImpulse.FromBullet(new Vector3(0f, 0f, 250f), damage: 30);
        var fast = RagdollImpulse.FromBullet(new Vector3(0f, 0f, 900f), damage: 30);

        Assert.Equal(slow.Length(), fast.Length(), 4);
    }

    [Fact]
    public void BlastShovesOutFromWhereItWentOff()
    {
        var origin = new Vector3(3f, 10f, 0f);
        var centre = new Vector3(5f, 10.5f, 0f);

        var impulse = RagdollImpulse.FromBlast(origin, centre, damage: 60f);

        Assert.True(impulse.X > 0f, "thrown away from the blast, not toward it");
        Assert.True(impulse.Y > 0f, "a charge below the centre of mass also lifts");
        Assert.Equal(
            Vector3.Normalize(centre - origin),
            Vector3.Normalize(impulse),
            new VectorComparer(1e-4f));
    }

    [Fact]
    public void GrenadeUnderfootThrowsStraightUp()
    {
        var origin = new Vector3(4f, 10f, -2f);
        var centre = origin + Vector3.UnitY;

        var impulse = RagdollImpulse.FromBlast(origin, centre, damage: 100f);

        Assert.Equal(0f, impulse.X, 5);
        Assert.Equal(0f, impulse.Z, 5);
        Assert.True(impulse.Y > 0f);
    }

    [Fact]
    public void HarderBlowsThrowFurtherUntilTheCap()
    {
        float pistol = RagdollImpulse.Speed(5f);
        float rifle = RagdollImpulse.Speed(30f);
        float headshot = RagdollImpulse.Speed(60f);

        Assert.True(pistol < rifle);
        Assert.True(rifle < headshot);
        Assert.True(headshot <= RagdollImpulse.MaxSpeed);
    }

    [Fact]
    public void AbsurdDamageIsCappedRatherThanLaunchingTheBody()
    {
        Assert.Equal(RagdollImpulse.MaxSpeed, RagdollImpulse.Speed(100_000f), 4);
        Assert.Equal(
            RagdollImpulse.MaxSpeed,
            RagdollImpulse.FromBullet(Vector3.UnitX, damage: ushort.MaxValue).Length(),
            4);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]        // a blast exactly at the body's centre
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(float.PositiveInfinity, 0f, 0f)]
    public void DegenerateDirectionsProduceNoShoveRatherThanANanCorpse(float x, float y, float z)
    {
        var impulse = RagdollImpulse.FromBullet(new Vector3(x, y, z), damage: 30);

        Assert.Equal(Vector3.Zero, impulse);
    }

    [Fact]
    public void NoDamageIsNoShove()
    {
        Assert.Equal(Vector3.Zero, RagdollImpulse.FromBullet(Vector3.UnitZ, damage: 0));
        Assert.Equal(Vector3.Zero, RagdollImpulse.FromBlast(Vector3.Zero, Vector3.UnitY, damage: 0f));
    }

    private sealed class VectorComparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => (a - b).Length() <= tolerance;
        public int GetHashCode(Vector3 value) => 0;
    }
}
