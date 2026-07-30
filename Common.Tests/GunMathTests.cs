using System.Numerics;

namespace Demiurge.Tests;

public class GunMathTests
{
    private static readonly Vector3 Feet = new(0f, 10f, 0f);

    /// <summary>Fires horizontally from 20 m away at the given height above the target's feet.</summary>
    private static float? ShotAtHeight(float height, float lateralOffset = 0f)
    {
        var origin = new Vector3(-20f, Feet.Y + height, lateralOffset);
        var target = new Vector3(0f, Feet.Y + height, lateralOffset);
        Vector3 delta = target - origin;
        float distance = delta.Length();
        return GunMath.PlayerHitDistance(origin, delta / distance, Feet, distance + 5f);
    }

    [Theory]
    // Feet, shins, hips, chest, shoulders, head. The head is the case the old single sphere centred
    // 0.5 m up could not represent at all, which is what made peeking over cover unhittable.
    [InlineData(0.05f)]
    [InlineData(0.4f)]
    [InlineData(0.9f)]
    [InlineData(1.2f)]
    [InlineData(1.5f)]
    [InlineData(1.75f)]
    public void StandingBodyIsHittableOverItsWholeHeight(float height)
        => Assert.NotNull(ShotAtHeight(height));

    [Theory]
    [InlineData(-0.35f)]
    [InlineData(2.2f)]
    public void ShotsClearOfTheBodyMiss(float height)
        => Assert.Null(ShotAtHeight(height));

    [Fact]
    public void EveryAiAimPointLiesInsideTheHittableBody()
    {
        // The contract between perception and hit detection: an AI must never settle on a body point
        // it can see but provably cannot damage.
        foreach (float height in GunConfig.AimHeights)
            Assert.NotNull(ShotAtHeight(height));
    }

    [Fact]
    public void LateralMissBeyondHitRadiusIsRejected()
    {
        Assert.NotNull(ShotAtHeight(0.9f, GunConfig.HitRadius - 0.05f));
        Assert.Null(ShotAtHeight(0.9f, GunConfig.HitRadius + 0.05f));
    }

    [Fact]
    public void ReportedDistanceIsTheClosestApproachAlongTheRay()
    {
        var origin = new Vector3(-20f, Feet.Y + 0.9f, 0f);
        float? distance = GunMath.PlayerHitDistance(
            origin,
            Vector3.UnitX,
            Feet,
            segmentLength: 100f);

        Assert.NotNull(distance);
        Assert.Equal(20f, distance.Value, 3);
    }

    [Fact]
    public void HitBeyondTheSweptSegmentIsRejected()
    {
        var origin = new Vector3(-20f, Feet.Y + 0.9f, 0f);

        Assert.Null(GunMath.PlayerHitDistance(origin, Vector3.UnitX, Feet, segmentLength: 5f));
        Assert.NotNull(GunMath.PlayerHitDistance(origin, Vector3.UnitX, Feet, segmentLength: 25f));
    }

    [Fact]
    public void ShotFromDirectlyAboveTravelsDownTheBodyAxis()
    {
        // The degenerate parallel case: a ray along the capsule axis must still register.
        var origin = new Vector3(0f, Feet.Y + 8f, 0f);
        Assert.NotNull(GunMath.PlayerHitDistance(origin, -Vector3.UnitY, Feet, segmentLength: 20f));
    }

    [Fact]
    public void ShotBehindTheMuzzleIsNotAHit()
    {
        var origin = new Vector3(-20f, Feet.Y + 0.9f, 0f);
        Assert.Null(GunMath.PlayerHitDistance(origin, -Vector3.UnitX, Feet, segmentLength: 100f));
    }
}
