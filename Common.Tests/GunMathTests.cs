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

    /// <summary>Fires horizontally from 20 m away at the given height, reporting the region hit.</summary>
    private static GunMath.PlayerHit? HitAtHeight(float height, bool crouching = false, float lateralOffset = 0f)
    {
        var origin = new Vector3(-20f, Feet.Y + height, lateralOffset);
        var target = new Vector3(0f, Feet.Y + height, lateralOffset);
        Vector3 delta = target - origin;
        float distance = delta.Length();
        return GunMath.PlayerHitAt(origin, delta / distance, Feet, distance + 5f, crouching);
    }

    [Theory]
    [InlineData(0.05f)]   // feet
    [InlineData(0.9f)]    // hips
    [InlineData(1.0f)]    // chest, just under the jaw at 1.047
    [InlineData(1.75f)]   // over the crown, still inside the capsule
    public void ShotsOutsideTheHeadSphereAreOrdinaryHits(float height)
        => Assert.False(HitAtHeight(height)?.Head);

    [Theory]
    [InlineData(1.26f)]   // centre
    [InlineData(1.10f)]   // jaw
    [InlineData(1.42f)]   // crown
    public void ShotsThroughTheHeadAreHeadHits(float height)
        => Assert.True(HitAtHeight(height)?.Head);

    [Fact]
    public void TheHeadIsAlwaysInsideTheBodyItBelongsTo()
    {
        // The multiplier can only ever apply to a hit, so the head sphere must not reach outside
        // the capsule — otherwise a 2x hit would exist that an ordinary hit does not.
        foreach (bool crouching in new[] { false, true })
        {
            float center = GunConfig.HeadCenterHeight - (crouching ? GunConfig.CrouchHeadDrop : 0f);
            Assert.True(center - GunConfig.HeadRadius >= 0f);
            Assert.True(center + GunConfig.HeadRadius <= PlayerMovement.Body.Height);
            Assert.True(GunConfig.HeadRadius <= GunConfig.HitRadius);
        }
    }

    [Fact]
    public void CrouchingCarriesTheHeadDownWithTheModel()
    {
        // The capsule ignores crouch; the head cannot, or a shot over a crouched man's head would
        // be worth double and one through it would not.
        Assert.True(HitAtHeight(1.26f - GunConfig.CrouchHeadDrop, crouching: true)?.Head);
        Assert.False(HitAtHeight(1.26f, crouching: true)?.Head);
        Assert.False(HitAtHeight(1.26f - GunConfig.CrouchHeadDrop, crouching: false)?.Head);
    }

    [Fact]
    public void AShotPastTheHeadInsideTheCapsuleIsNotAHeadHit()
    {
        // The capsule is 0.6 m wide and the head 0.2 m: the metre between them is body, not head.
        var hit = HitAtHeight(GunConfig.HeadCenterHeight, lateralOffset: 0.45f);
        Assert.NotNull(hit);
        Assert.False(hit.Value.Head);
    }

    [Fact]
    public void HeadshotDamageDoublesAndSaturates()
    {
        Assert.Equal((ushort)140, GunConfig.Headshot(70));
        Assert.Equal(ushort.MaxValue, GunConfig.Headshot(ushort.MaxValue));
    }
}
