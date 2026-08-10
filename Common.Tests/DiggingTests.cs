using System.Numerics;
using Xunit;

namespace Demiurge.Tests;

public class DiggingTests
{
    /// <summary>Solid below the surface, so "was it dug" is just a sign test.</summary>
    static bool IsSolid(ChunkMap map, Vector3 p)
        => TerrainCollision.TrySampleRaw(map, p, out float d) && d < 0f;

    [Fact]
    public void TargetVoxelPicksTheSolidSideOfTheSurface()
    {
        // Flat ground at 12.5: the surface sits between grid points 12 and 13, and 12 is the solid
        // one. A hit exactly on the surface must resolve downward, not up into the air.
        var hit = new Vector3(4f, SyntheticTerrain.GroundHeight, 7f);

        var target = Digging.TargetVoxel(hit, Vector3.UnitY);

        Assert.Equal(new Vector3(4f, 12f, 7f), target);
    }

    [Fact]
    public void TargetVoxelFollowsTheNormalOnAWall()
    {
        // Wall face at x = 6 with its outward normal pointing back down -X; the solid voxel is the
        // one at greater x, not the air the ray came through.
        var target = Digging.TargetVoxel(new Vector3(6f, 20f, 3f), -Vector3.UnitX);

        Assert.Equal(new Vector3(6f, 20f, 3f), target);
        Assert.True(target.X >= 6f, "should step into the wall, not out of it");
    }

    [Fact]
    public void DiggingRemovesTheTargetedVoxel()
    {
        var map = SyntheticTerrain.Flat();
        var target = new Vector3(4f, 12f, 7f);

        Assert.True(IsSolid(map, target));

        TerrainEdits.ApplyBox(map, target, Digging.Bite, EditMode.Subtract, BlockType.BlockType_Air);

        Assert.False(IsSolid(map, target));
    }

    [Fact]
    public void PartialDigsMatchOneFullBiteAtTheTarget()
    {
        var full = SyntheticTerrain.Flat();
        var partial = SyntheticTerrain.Flat();
        var target = new Vector3(4f, 12f, 7f);

        TerrainEdits.ApplyBox(full, target, Digging.Bite, EditMode.Subtract, BlockType.BlockType_Air);

        for (int i = 0; i < Digging.ClicksPerVoxel; i++)
            TerrainEdits.ApplyBox(partial, target, Digging.Bite, EditMode.Subtract, BlockType.BlockType_Air,
                EditShape.Sphere, Digging.BiteStrength);

        Assert.True(TerrainCollision.TrySampleRaw(full, target, out float fullDensity));
        Assert.True(TerrainCollision.TrySampleRaw(partial, target, out float partialDensity));
        Assert.Equal(fullDensity, partialDensity, 4);
    }

    /// <summary>
    /// The case that fails silently. A bite centred on a chunk border spans two chunks, and the
    /// per-chunk ApplyBox clamps to its own — so a map-wide edit that forgot to iterate would carve
    /// one side of the seam and leave the other solid, leaving a half-dug voxel with a wall through
    /// the middle of it.
    /// </summary>
    [Theory]
    [InlineData(0)]    // x = 0 is a chunk origin: the bite straddles chunks -1 and 0
    [InlineData(16)]   // and again at the next border along
    public void DiggingOnAChunkBorderCarvesBothSides(int borderX)
    {
        var map = SyntheticTerrain.Flat(); // chunks -1..1 in both axes
        var target = new Vector3(borderX, 12f, 7f);

        TerrainEdits.ApplyBox(map, target, Digging.Bite, EditMode.Subtract, BlockType.BlockType_Air);

        Assert.False(IsSolid(map, target));

        // The margin either side must have been rewritten too, in BOTH chunks — that is what makes
        // the surface land in the same place on each side of the seam.
        Assert.True(TerrainCollision.TrySampleRaw(map, new Vector3(borderX - 1f, 12f, 7f), out float left));
        Assert.True(TerrainCollision.TrySampleRaw(map, new Vector3(borderX + 1f, 12f, 7f), out float right));
        Assert.Equal(left, right, 0.001f);
    }

    [Fact]
    public void EditedBoundsCoverEverythingTheEditTouched()
    {
        var map = SyntheticTerrain.Flat();
        var target = new Vector3(3f, 12f, 3f);

        // Sampled well wider than any plausible report, so the check can actually catch a report
        // that is too small.
        var before = Field(map, target - new Vector3(8f), target + new Vector3(8f));
        var (rmin, rmax) = TerrainEdits.ApplyBox(map, target, Digging.Bite, EditMode.Subtract, BlockType.BlockType_Air);

        // The reported bounds are what gets re-meshed, so nothing the edit CHANGED may lie outside
        // them — anything that did would keep its old triangles until something else happened to
        // dirty that section. Compare a region wider than the report and check the difference is
        // contained by it.
        foreach (var (p, was) in before)
        {
            Assert.True(TerrainCollision.TrySampleRaw(map, p, out float now));
            if (Math.Abs(now - was) < 1e-6f) continue;

            Assert.True(p.X >= rmin.X && p.X <= rmax.X
                     && p.Y >= rmin.Y && p.Y <= rmax.Y
                     && p.Z >= rmin.Z && p.Z <= rmax.Z,
                $"{p} changed but sits outside the reported bounds {rmin}..{rmax}");
        }

        static List<(Vector3, float)> Field(ChunkMap map, Vector3 min, Vector3 max)
        {
            var samples = new List<(Vector3, float)>();
            for (float x = min.X; x <= max.X; x += 1f)
                for (float y = min.Y; y <= max.Y; y += 1f)
                    for (float z = min.Z; z <= max.Z; z += 1f)
                    {
                        var p = new Vector3(x, y, z);
                        if (TerrainCollision.TrySampleRaw(map, p, out float d)) samples.Add((p, d));
                    }
            return samples;
        }
    }

    /// <summary>
    /// Placing is digging with the sign flipped, so the two brushes must land on opposite sides of
    /// the same surface — asserted as that relationship rather than as two coordinates, since what
    /// matters is that they never pick the same voxel however the hit rounds.
    /// </summary>
    [Theory]
    [InlineData(0f, 1f, 0f)]     // floor
    [InlineData(-1f, 0f, 0f)]    // wall
    [InlineData(0f, -1f, 0f)]    // ceiling
    public void PlacementLandsOnTheAirSideOfWhateverDiggingTakesOut(float nx, float ny, float nz)
    {
        var normal = new Vector3(nx, ny, nz);

        // Half a voxel out from a grid point along the normal, which is where a sign change actually
        // puts a surface. A hit ON a grid point is the degenerate case — both steps land on a
        // midpoint and round to the same voxel — and it is not a case the field can produce.
        var hit = new Vector3(4f, 12f, 7f) + normal * 0.5f;

        var dug = Digging.TargetVoxel(hit, normal);
        var placed = Digging.PlacementVoxel(hit, normal);

        Assert.NotEqual(dug, placed);
        // The step is along the normal, so the placed voxel is the further one out of the solid.
        Assert.True(Vector3.Dot(placed - dug, normal) > 0f);
    }

    /// <summary>
    /// The placement half of <see cref="PartialDigsMatchOneFullBiteAtTheTarget"/>, and the same
    /// property: however many clicks a voxel is spread over, the finished field is the one full
    /// brush would have written. That is the whole meaning of "inverse of the dig" — the two run the
    /// same brush at the same strength, and neither is allowed its own arithmetic.
    /// </summary>
    [Fact]
    public void TwoClicksPlaceAWholeVoxel()
    {
        var full = SyntheticTerrain.Flat();
        var partial = SyntheticTerrain.Flat();

        // The first air sample above the surface, which is where a placement aimed at the ground
        // lands. Building into open air is not reachable: the target always comes off a raycast hit.
        var target = new Vector3(4f, 13f, 7f);

        Assert.False(IsSolid(partial, target));

        TerrainEdits.ApplyBox(
            full, target, Digging.Bite, EditMode.Add, Digging.PlacedBlock, EditShape.Sphere);

        for (int i = 0; i < Digging.ClicksPerVoxel; i++)
            TerrainEdits.ApplyBox(
                partial, target, Digging.Bite, EditMode.Add, Digging.PlacedBlock,
                EditShape.Sphere, Digging.BiteStrength);

        Assert.True(IsSolid(partial, target));
        Assert.True(TerrainCollision.TrySampleRaw(full, target, out float fullDensity));
        Assert.True(TerrainCollision.TrySampleRaw(partial, target, out float partialDensity));
        Assert.Equal(fullDensity, partialDensity, 4);

        // And it is made of what was placed, not of whatever the terrain around it is.
        Assert.True(partial.TryGetVoxel(4, 13, 7, out var voxel));
        Assert.Equal(Digging.PlacedBlock, voxel.Material);
    }

    /// <summary>A player may not build the block they are standing in — the one placement rule that
    /// is not taste, since the brush lands where the body is whenever you look down.</summary>
    [Fact]
    public void PlacementIsRefusedInsideTheBuildersOwnBody()
    {
        var feet = new Vector3(0f, 12f, 0f);

        Assert.True(Digging.WouldEncasePlayer(feet, new Vector3(0f, 13f, 0f)));   // chest height
        Assert.False(Digging.WouldEncasePlayer(feet, new Vector3(0f, 16f, 0f)));  // overhead
        Assert.False(Digging.WouldEncasePlayer(feet, new Vector3(3f, 13f, 0f)));  // an arm away
    }

    [Fact]
    public void ReachIsMeasuredFromTheEye()
    {
        var feet = new Vector3(0f, 12f, 0f);

        // The eye is where the client casts from, so the server has to measure from there too or it
        // rejects digs at the edge of a reach the player was shown as reachable.
        Assert.Equal(feet + new Vector3(0f, Digging.EyeHeight, 0f), Digging.Eye(feet));

        // Straight down at your own feet: close by any sane reading, and it must not be rejected
        // just because the measurement started somewhere else on the body.
        Assert.True(Digging.InReach(feet, new Vector3(0f, 11f, 0f)));

        // Well beyond arm's length, which is the thing the server actually has to refuse.
        Assert.False(Digging.InReach(feet, new Vector3(40f, 12f, 0f)));
    }
}
