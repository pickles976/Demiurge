using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the firing solution, not a trace through it. The one that matters most is the last:
/// a solved velocity must actually put the bomb on the point it was solved for, because everything
/// else about the weapon — the sector, the range band, the aim marker — is a promise that it will.
/// </summary>
public class MortarBallisticsTests
{
    private static readonly Vector3 Emplacement = new(10f, 30f, -5f);
    private const float FacingNorth = 0f;    // +Z, per the yaw convention

    private static Vector3 Muzzle(Vector3 mortar)
        => mortar + Vector3.UnitY * MortarBallistics.MuzzleHeight;

    [Fact]
    public void ATargetInsideTheSectorAndBandIsLeftAlone()
    {
        var asked = Emplacement + new Vector3(0f, 0f, 120f);   // dead ahead, mid band

        var clamped = MortarBallistics.ClampTarget(Emplacement, FacingNorth, asked);

        Assert.Equal(asked.X, clamped.X, 3);
        Assert.Equal(asked.Z, clamped.Z, 3);
    }

    [Fact]
    public void RangeIsClampedToTheBandAtBothEnds()
    {
        var tooClose = Emplacement + new Vector3(0f, 0f, 5f);
        var tooFar = Emplacement + new Vector3(0f, 0f, 900f);

        float near = Flat(MortarBallistics.ClampTarget(Emplacement, FacingNorth, tooClose) - Emplacement);
        float far = Flat(MortarBallistics.ClampTarget(Emplacement, FacingNorth, tooFar) - Emplacement);

        Assert.Equal(MortarConfig.MinimumRange, near, 2);
        Assert.Equal(MortarConfig.MaximumRange, far, 2);
    }

    [Fact]
    public void BearingIsClampedToTheSectorButRangeSurvivesIt()
    {
        // Far off to one side, at a legal distance: the bearing must fold back to the sector edge
        // and the distance must NOT be sacrificed doing it.
        var wayLeft = Emplacement + new Vector3(-150f, 0f, 20f);

        var clamped = MortarBallistics.ClampTarget(Emplacement, FacingNorth, wayLeft);
        var offset = clamped - Emplacement;

        float bearing = MathF.Atan2(offset.X, offset.Z);
        float limit = MortarConfig.SectorHalfAngleDegrees * MathF.PI / 180f;

        Assert.Equal(limit, MathF.Abs(bearing), 3);
        Assert.Equal(Flat(wayLeft - Emplacement), Flat(offset), 2);
        Assert.True(MortarBallistics.IsLegalTarget(Emplacement, FacingNorth, clamped));
    }

    [Fact]
    public void TheSectorFollowsTheHeadingItWasEmplacedOn()
    {
        // Same asked-for point, two emplacements laid on different headings: one has it in sector,
        // the other does not. The heading is the whole difference.
        var eastward = Emplacement + new Vector3(120f, 0f, 0f);

        Assert.True(MortarBallistics.IsLegalTarget(Emplacement, MathF.PI / 2f, eastward));
        Assert.False(MortarBallistics.IsLegalTarget(Emplacement, FacingNorth, eastward));
    }

    [Fact]
    public void AFabricatedTargetOutsideTheSectorIsRefused()
    {
        var behind = Emplacement + new Vector3(0f, 0f, -120f);
        Assert.False(MortarBallistics.IsLegalTarget(Emplacement, FacingNorth, behind));
    }

    [Fact]
    public void FireSectorRequiresBothLegalBearingAndRange()
    {
        var inside = Emplacement + new Vector3(0f, 0f, 120f);
        var tooClose = Emplacement + new Vector3(0f, 0f, MortarConfig.MinimumRange - 1f);
        var tooFar = Emplacement + new Vector3(0f, 0f, MortarConfig.MaximumRange + 1f);
        var outsideTraverse = Emplacement + new Vector3(120f, 0f, 0f);

        Assert.True(MortarBallistics.IsTargetInFireSector(Emplacement, FacingNorth, inside));
        Assert.False(MortarBallistics.IsTargetInFireSector(Emplacement, FacingNorth, tooClose));
        Assert.False(MortarBallistics.IsTargetInFireSector(Emplacement, FacingNorth, tooFar));
        Assert.False(MortarBallistics.IsTargetInFireSector(Emplacement, FacingNorth, outsideTraverse));
    }

    /// <summary>
    /// The load-bearing one: fly the solved velocity under the same gravity the server uses and the
    /// bomb has to arrive where it was aimed. Integrated in small steps rather than solved in closed
    /// form, so this tests the trajectory the game will actually produce.
    /// </summary>
    [Theory]
    [InlineData(50f)]
    [InlineData(90f)]
    [InlineData(140f)]
    [InlineData(200f)]
    public void ASolvedRoundLandsOnItsTarget(float range)
    {
        var target = Emplacement + new Vector3(0f, 0f, range);
        var muzzle = Muzzle(Emplacement);

        var velocity = MortarBallistics.SolveVelocity(muzzle, target, ProjectileMotion.Gravity);
        Assert.NotNull(velocity);

        var position = muzzle;
        var v = velocity!.Value;
        const float dt = 1f / 600f;
        for (int step = 0; step < 600 * 30 && position.Y >= target.Y; step++)
        {
            v -= Vector3.UnitY * ProjectileMotion.Gravity * dt;
            position += v * dt;
        }

        Assert.Equal(target.X, position.X, 0);
        Assert.Equal(target.Z, position.Z, 0);
    }

    [Fact]
    public void ARoundIsThrownHarderTheFurtherItHasToGo()
    {
        var muzzle = Muzzle(Emplacement);
        float near = MortarBallistics
            .SolveVelocity(muzzle, Emplacement + new Vector3(0f, 0f, 60f), ProjectileMotion.Gravity)!
            .Value.Length();
        float far = MortarBallistics
            .SolveVelocity(muzzle, Emplacement + new Vector3(0f, 0f, 190f), ProjectileMotion.Gravity)!
            .Value.Length();

        Assert.True(far > near);
    }

    /// <summary>
    /// A target far enough above the tube that a 70-degree throw cannot reach it at any speed. The
    /// arithmetic goes singular there, and the answer has to be "no shot" rather than a NaN round
    /// flying off the map.
    /// </summary>
    [Fact]
    public void ATargetAboveTheReachOfTheArcHasNoSolution()
    {
        var muzzle = Muzzle(Emplacement);
        var upACliff = Emplacement + new Vector3(0f, 400f, 60f);

        Assert.Null(MortarBallistics.SolveVelocity(muzzle, upACliff, ProjectileMotion.Gravity));
    }

    [Fact]
    public void ARoundStillReachesATargetBelowTheTube()
    {
        var muzzle = Muzzle(Emplacement);
        var downhill = Emplacement + new Vector3(0f, -25f, 120f);

        var velocity = MortarBallistics.SolveVelocity(muzzle, downhill, ProjectileMotion.Gravity);

        Assert.NotNull(velocity);
        Assert.True(velocity!.Value.Y > 0f, "a mortar throws its bomb up even when shooting downhill");
    }

    /// <summary>Scatter is across the ground, never in height — a bomb that dispersed vertically
    /// would go off in the air.</summary>
    [Fact]
    public void ScatterStaysOnTheGroundPlane()
    {
        var aim = new Vector3(0f, 12f, 0f);
        var random = new Random(1);

        for (int i = 0; i < 50; i++)
            Assert.Equal(aim.Y, MortarBallistics.Disperse(aim, random).Y);
    }

    private static float Flat(Vector3 v) => new Vector3(v.X, 0f, v.Z).Length();
}
