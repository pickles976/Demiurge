using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Shot geometry against a standing trunk. The interesting cases are all about the trunk being TALL
/// and THIN, which is the property a sphere at the object's origin cannot express.
/// </summary>
public class TrunkHitTests
{
    private const float Radius = TreePlacement.TrunkRadius;
    private const float Height = TreePlacement.TrunkHeight;

    /// <summary>A trunk standing at the origin, sunk the way a placed tree is.</summary>
    private static readonly Vector3 Base = new(0f, -TreePlacement.SinkDepth, 0f);

    private static float? Cast(Vector3 from, Vector3 towards, float length = 100f)
        => GunMath.TrunkHitDistance(
            from, Vector3.Normalize(towards), Base, Radius, Height, length);

    [Fact]
    public void StopsAShotAtChestHeight()
    {
        float? hit = Cast(new Vector3(-10f, 1.2f, 0f), Vector3.UnitX);

        Assert.NotNull(hit);
        Assert.Equal(10f - Radius, hit!.Value, 2);
    }

    [Fact]
    public void StopsAShotHighOnTheTrunk()
    {
        // Well above a player, still well below the top: a sphere at the base would miss this.
        Assert.NotNull(Cast(new Vector3(-10f, 6f, 0f), Vector3.UnitX));
    }

    [Fact]
    public void LetsAShotOverTheTopThrough()
    {
        Assert.Null(Cast(new Vector3(-10f, Height + 1f, 0f), Vector3.UnitX));
    }

    [Fact]
    public void LetsAShotPastTheSideThrough()
    {
        // A metre wide of a half-metre trunk. The player capsule radius is 0.6, so a sphere sized
        // for an actor would wrongly swallow this one.
        Assert.Null(Cast(new Vector3(-10f, 1.2f, 1f), Vector3.UnitX));
    }

    [Fact]
    public void StopsAShotClippingTheEdge()
    {
        Assert.NotNull(Cast(new Vector3(-10f, 1.2f, Radius - 0.05f), Vector3.UnitX));
    }

    [Fact]
    public void IgnoresATrunkBehindTheShooter()
    {
        Assert.Null(Cast(new Vector3(10f, 1.2f, 0f), Vector3.UnitX));
    }

    [Fact]
    public void IgnoresATrunkBeyondTheSegment()
    {
        Assert.Null(Cast(new Vector3(-10f, 1.2f, 0f), Vector3.UnitX, length: 5f));
    }

    /// <summary>
    /// Fired from inside the trunk, the round leaves rather than being trapped. Someone with his
    /// back against a tree has to be able to shoot.
    /// </summary>
    [Fact]
    public void AShotFromInsideLeaves()
    {
        float? hit = Cast(new Vector3(0f, 1.2f, 0f), Vector3.UnitX);

        Assert.NotNull(hit);
        Assert.Equal(Radius, hit!.Value, 2);
    }

    [Fact]
    public void AVerticalShotIsNotStopped()
    {
        Assert.Null(Cast(new Vector3(0.2f, 1.2f, 0.2f), Vector3.UnitY));
    }

    /// <summary>The sunk half metre is still trunk: a shot at ankle height hits wood.</summary>
    [Fact]
    public void StopsAShotAtTheFoot()
    {
        Assert.NotNull(Cast(new Vector3(-10f, 0.1f, 0f), Vector3.UnitX));
    }
}
