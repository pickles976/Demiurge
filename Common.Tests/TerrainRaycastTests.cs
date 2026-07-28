using System.Numerics;
using Xunit;

namespace Demiurge.Tests;

/// <summary>
/// Rays against the hand-built fields in <see cref="SyntheticTerrain"/>. Every expected hit is
/// arithmetic off an exact distance function, so these pin down sub-voxel accuracy rather than
/// "a hit happened somewhere".
/// </summary>
public class TerrainRaycastTests
{
    /// <summary>Bisection stops around a hundredth of a voxel; allow a shade more.</summary>
    const float Tolerance = 0.02f;

    [Fact]
    public void StraightDownOntoFlatGroundHitsTheSurface()
    {
        var map = SyntheticTerrain.Flat();

        var hit = TerrainRaycast.Cast(map, new Vector3(4.5f, 30f, 4.5f), -Vector3.UnitY, 100f);

        Assert.NotNull(hit);
        Assert.Equal(SyntheticTerrain.GroundHeight, hit!.Value.Point.Y, Tolerance);
        Assert.Equal(30f - SyntheticTerrain.GroundHeight, hit.Value.Distance, Tolerance);

        // Flat ground: straight up, and normalised.
        Assert.Equal(1f, hit.Value.Normal.Y, Tolerance);
        Assert.Equal(1f, hit.Value.Normal.Length(), Tolerance);
    }

    [Fact]
    public void HorizontalRayHitsAWallAtItsFace()
    {
        // Wall fills x >= 6; the ray flies well above the ground so only the wall can stop it.
        var map = SyntheticTerrain.Wall(wallX: 6f);

        var hit = TerrainRaycast.Cast(map, new Vector3(-10f, 20f, 3.5f), Vector3.UnitX, 100f);

        Assert.NotNull(hit);
        Assert.Equal(6f, hit!.Value.Point.X, Tolerance);
        Assert.Equal(16f, hit.Value.Distance, Tolerance);

        // Facing back down -X, toward where the ray came from.
        Assert.Equal(-1f, hit.Value.Normal.X, Tolerance);
    }

    [Fact]
    public void RayPastTheSurfaceMisses()
    {
        var map = SyntheticTerrain.Flat();

        // Starts above the ground and climbs: nothing to hit.
        Assert.Null(TerrainRaycast.Cast(map, new Vector3(0f, 20f, 0f), Vector3.UnitY, 100f));
    }

    [Fact]
    public void MaxDistanceStopsTheRayShortOfARealSurface()
    {
        var map = SyntheticTerrain.Flat();
        var origin = new Vector3(0f, 30f, 0f);

        // The ground is 17.5 below. Stopping at 10 must miss, and a longer ray must not.
        Assert.Null(TerrainRaycast.Cast(map, origin, -Vector3.UnitY, 10f));
        Assert.NotNull(TerrainRaycast.Cast(map, origin, -Vector3.UnitY, 25f));
    }

    [Fact]
    public void RayStartingInsideTerrainHitsAtItsOrigin()
    {
        var map = SyntheticTerrain.Flat();
        var origin = new Vector3(2.5f, SyntheticTerrain.GroundHeight - 4f, 2.5f);

        var hit = TerrainRaycast.Cast(map, origin, Vector3.UnitX, 50f);

        Assert.NotNull(hit);
        Assert.Equal(0f, hit!.Value.Distance, Tolerance);
        Assert.Equal(origin, hit.Value.Point);
    }

    [Fact]
    public void RayLeavingLoadedTerrainMisses()
    {
        // One chunk only: a ray heading out of it runs off the loaded world.
        var map = SyntheticTerrain.BuildOne(new ChunkIndex { x = 0, z = 0 },
            (x, y, z) => y - SyntheticTerrain.GroundHeight);

        Assert.Null(TerrainRaycast.Cast(map, new Vector3(8f, 20f, 8f), Vector3.UnitX, 200f));
    }

    /// <summary>
    /// The case that makes the gradient correction load-bearing. The stored field is the VERTICAL
    /// gap to the surface, which on a slope overstates the true distance by 1/cos(slope) — step by
    /// the raw value and the ray jumps straight through the hillside. At 60 degrees the overstatement
    /// is a factor of two, so a miss here means the correction was dropped.
    /// </summary>
    [Theory]
    [InlineData(30f)]
    [InlineData(45f)]
    [InlineData(60f)]
    public void SteepSlopeIsNotTunnelledThrough(float degrees)
    {
        var map = SyntheticTerrain.Slope(degrees);
        float rise = MathF.Tan(degrees * (MathF.PI / 180f));

        // Straight down at x = 6, where the surface sits at GroundHeight + rise * 6.
        float expected = SyntheticTerrain.GroundHeight + rise * 6f;
        var hit = TerrainRaycast.Cast(map, new Vector3(6f, expected + 8f, 3.5f), -Vector3.UnitY, 60f);

        Assert.NotNull(hit);
        Assert.Equal(expected, hit!.Value.Point.Y, 0.1f);

        // The normal leans away from the uphill direction (+X), and stays a unit vector.
        Assert.True(hit.Value.Normal.X < 0f, $"normal should lean downhill, was {hit.Value.Normal}");
        Assert.Equal(1f, hit.Value.Normal.Length(), Tolerance);
    }

    /// <summary>
    /// A floating slab has air above AND below, so a ray from underneath must stop at its underside
    /// rather than at the first surface a heightmap would report.
    ///
    /// This is also the case that catches an overstepping march. Approaching through saturated air,
    /// the gradient collapses at the saturation boundary and the CORRECTED distance reads far larger
    /// than the truth — 9.4 against a real gap of 3.0, measured. A march that trusts it jumps the
    /// whole slab and reports the clean miss this test started life failing on.
    /// </summary>
    [Fact]
    public void RayFromBelowHitsTheUndersideOfAnOverhang()
    {
        var map = SyntheticTerrain.Slab(lowY: 20f, highY: 24f);

        var hit = TerrainRaycast.Cast(map, new Vector3(1.5f, 14f, 1.5f), Vector3.UnitY, 40f);

        Assert.NotNull(hit);
        Assert.Equal(20f, hit!.Value.Point.Y, Tolerance);
        Assert.Equal(-1f, hit.Value.Normal.Y, Tolerance);   // pointing down, out of the underside
    }

    /// <summary>
    /// The step bound under pressure: a slab thinner than one saturated step (2.54 voxels) is what a
    /// too-eager march skips entirely. Approach from far enough below that the ray arrives at full
    /// stride.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3f)]
    [InlineData(6f)]
    public void ThinSlabsAreNotSteppedOver(float thickness)
    {
        const float low = 24f;
        var map = SyntheticTerrain.Slab(lowY: low, highY: low + thickness);

        var hit = TerrainRaycast.Cast(map, new Vector3(1.5f, 2f, 1.5f), Vector3.UnitY, 60f);

        Assert.NotNull(hit);
        Assert.Equal(low, hit!.Value.Point.Y, 0.1f);
    }

    [Fact]
    public void DirectionNeedNotBeNormalised()
    {
        var map = SyntheticTerrain.Flat();
        var origin = new Vector3(4.5f, 30f, 4.5f);

        var unit = TerrainRaycast.Cast(map, origin, -Vector3.UnitY, 100f);
        var scaled = TerrainRaycast.Cast(map, origin, new Vector3(0f, -7f, 0f), 100f);

        Assert.NotNull(unit);
        Assert.NotNull(scaled);
        Assert.Equal(unit!.Value.Distance, scaled!.Value.Distance, Tolerance);
    }

    [Fact]
    public void DegenerateInputsMissRatherThanSpin()
    {
        var map = SyntheticTerrain.Flat();
        var origin = new Vector3(0f, 30f, 0f);

        Assert.Null(TerrainRaycast.Cast(map, origin, Vector3.Zero, 100f));
        Assert.Null(TerrainRaycast.Cast(map, origin, -Vector3.UnitY, 0f));
        Assert.Null(TerrainRaycast.Cast(map, origin, -Vector3.UnitY, -5f));
    }
}
