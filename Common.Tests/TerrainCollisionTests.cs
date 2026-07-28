using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// The field queries collision is built on. These are the layer where being off by half a voxel or
/// by a factor of 1/cos(slope) is still a readable number rather than a player stuck in a hill.
/// </summary>
public class TerrainCollisionTests
{
    const float Ground = SyntheticTerrain.GroundHeight;

    // ---- Sampling grid ----

    /// <summary>
    /// The sample grid must be the mesher's: voxel (x, y, z) is the field at exactly (x, y, z). Half a
    /// voxel of offset here puts collision half a voxel away from the surface that gets drawn.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(1.5f)]
    [InlineData(-2.25f)]
    public void FlatGroundDistanceIsHeightAboveSurface(float above)
    {
        var map = SyntheticTerrain.Flat();

        Assert.True(TerrainCollision.TrySampleRaw(map, new Vector3(3.7f, Ground + above, -5.2f), out float d));
        Assert.Equal(above, d, 3);
    }

    [Fact]
    public void FlatGroundNormalPointsStraightUp()
    {
        var map = SyntheticTerrain.Flat();

        Assert.True(TerrainCollision.TrySample(map, new Vector3(1.3f, Ground + 0.4f, 2.6f), out var point));
        Assert.Equal(0f, point.Normal.X, 3);
        Assert.Equal(1f, point.Normal.Y, 3);
        Assert.Equal(0f, point.Normal.Z, 3);
    }

    // ---- The correction that makes the field usable as a distance ----

    /// <summary>
    /// The stored field is a VERTICAL gap, so on a slope it overstates the distance to the surface by
    /// 1/cos(slope). TrySample divides it out; without that, resolving a sphere against it leaves the
    /// sphere sunk into every slope by radius * (1 - cos(slope)).
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(20f)]
    [InlineData(30f)]
    [InlineData(45f)]
    public void SlopeDistanceIsCorrectedByGradientLength(float degrees)
    {
        var map = SyntheticTerrain.Slope(degrees);
        var probe = new Vector3(0f, Ground + 1f, 0f);       // exactly 1 voxel above the surface, vertically

        Assert.True(TerrainCollision.TrySampleRaw(map, probe, out float raw));
        Assert.True(TerrainCollision.TrySample(map, probe, out var point));

        float cos = MathF.Cos(degrees * (MathF.PI / 180f));

        Assert.Equal(1f, raw, 2);                          // the stored value ignores the slope...
        Assert.Equal(cos, point.Distance, 2);              // ...the corrected one is the true distance
    }

    /// <summary>
    /// A steep cell still has enough unsaturated corners to classify its local trilinear gradient.
    /// The smoothed pushout normal can pull in saturated neighboring samples, so standability uses
    /// SurfaceNormal while collision keeps Normal stable across cell boundaries.
    /// </summary>
    [Fact]
    public void SteepSlopeClassificationUsesTheLocalCellGradient()
    {
        var map = SyntheticTerrain.Slope(60f);
        var probe = new Vector3(0f, Ground + 1f, 0f);

        Assert.True(TerrainCollision.TrySampleRaw(map, probe, out float raw));
        Assert.True(TerrainCollision.TrySample(map, probe, out var point));

        const float True60 = 0.5f;                          // cos(60)

        Assert.True(MathF.Abs(point.Distance - True60) < 0.05f, $"corrected: {point.Distance}");
        Assert.True(MathF.Abs(raw - True60) > 0.4f, $"raw was already close, so the test proves nothing: {raw}");
        Assert.Equal(True60, point.SurfaceNormal.Y, 2);
    }

    /// <summary>The gradient DIRECTION never needed correcting — for `y - h(x,z)` it is already the normal.</summary>
    [Fact]
    public void SlopeNormalMatchesTheSurface()
    {
        var map = SyntheticTerrain.Slope(45f);

        Assert.True(TerrainCollision.TrySample(map, new Vector3(0f, Ground + 1f, 0f), out var point));

        var expected = Vector3.Normalize(new Vector3(-1f, 1f, 0f));
        Assert.Equal(expected.X, point.Normal.X, 2);
        Assert.Equal(expected.Y, point.Normal.Y, 2);
        Assert.Equal(0f, point.Normal.Z, 2);
    }

    // ---- Degenerate data ----

    /// <summary>
    /// Deep inside terrain every sample in the stencil is at the quantization clamp, so the difference
    /// is exactly zero: no length to divide by and no direction to escape along. Must still report
    /// "inside" and hand back a usable direction rather than a NaN or a zero vector.
    /// </summary>
    [Fact]
    public void BuriedInClampedFieldReportsInsideAndPushesUp()
    {
        var map = SyntheticTerrain.Solid();

        Assert.True(TerrainCollision.TrySample(map, new Vector3(2.5f, 60.5f, -1.5f), out var point));
        Assert.True(point.Distance < 0f);
        Assert.Equal(Vector3.UnitY, point.Normal);
    }

    /// <summary>
    /// No data is never air. Substituting air would let a player walk off the streamed world and fall,
    /// and would erase the world edge the finite generated region relies on.
    /// </summary>
    [Fact]
    public void UnloadedChunkFailsRatherThanReadingAsAir()
    {
        var map = SyntheticTerrain.BuildOne(new ChunkIndex { x = 0, z = 0 }, (x, y, z) => y - Ground);

        Assert.True(TerrainCollision.TrySample(map, new Vector3(8f, Ground + 1f, 8f), out _));
        Assert.False(TerrainCollision.TrySample(map, new Vector3(20f, Ground + 1f, 8f), out _));
    }

    /// <summary>
    /// A chunk border is invisible to the field: TryGetVoxel crosses it, so the interpolated distance
    /// either side of x = 16 is the same function. If it were not, players would trip on chunk seams.
    /// </summary>
    [Fact]
    public void DistanceIsContinuousAcrossAChunkBorder()
    {
        const float Degrees = 20f;
        const float Above = 0.4f;   // close to the surface, where the field is nowhere near the clamp

        var map = SyntheticTerrain.Slope(Degrees);
        float radians = Degrees * (MathF.PI / 180f);
        float expected = Above * MathF.Cos(radians);

        // x = 16 is a chunk boundary. Tracking the surface means every probe is the same distance from
        // it, so any seam shows up as a jump in a value that should be constant.
        for (float x = 15f; x <= 17f; x += 0.25f)
        {
            var probe = new Vector3(x, Ground + MathF.Tan(radians) * x + Above, 0f);

            Assert.True(TerrainCollision.TrySample(map, probe, out var point));
            Assert.InRange(point.Distance, expected - 0.03f, expected + 0.03f);
        }
    }

    // ---- Body sampling ----

    /// <summary>
    /// The deepest contact must be the one that is actually deepest, which for a body standing on the
    /// floor is the foot sphere — pushing out of anything else first would leave it embedded.
    /// </summary>
    [Fact]
    public void DeepestContactOnFlatGroundIsTheFootSphere()
    {
        var map = SyntheticTerrain.Flat();
        var body = PlayerMovement.Body;
        var feet = new Vector3(0f, Ground, 0f);

        Assert.True(TerrainCollision.TryDeepestContact(map, body, feet, out var deepest));
        Assert.True(TerrainCollision.TrySample(map, body.SampleCenter(feet, 0), out var foot));

        Assert.Equal(foot.Distance, deepest.Distance, 4);
    }

    /// <summary>Sample spheres must overlap, or a thin ledge slips between two of them.</summary>
    [Fact]
    public void SampleSpheresOverlapAlongTheBody()
    {
        var body = PlayerMovement.Body;
        var feet = Vector3.Zero;

        for (int i = 1; i < CapsuleBody.SampleCount; i++)
        {
            float spacing = body.SampleCenter(feet, i).Y - body.SampleCenter(feet, i - 1).Y;
            Assert.True(spacing < 2f * body.Radius, $"spheres {i - 1} and {i} leave a gap: {spacing} >= {2f * body.Radius}");
        }
    }
}
