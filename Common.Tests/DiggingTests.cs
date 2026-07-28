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

    [Fact]
    public void BrushRingLandsOnTheSurface()
    {
        var map = SyntheticTerrain.Flat();          // ground at 12.5
        var target = new Vector3(5f, 12f, 5f);      // topmost solid sample
        Span<Vector3> ring = new Vector3[16];

        int count = Digging.ProjectedRing(map, target, Vector3.UnitY, ring);
        Assert.Equal(16, count);

        foreach (var p in ring)
        {
            // Every point projected onto the isosurface, not left hovering above it.
            Assert.True(TerrainCollision.TrySample(map, p, out var field));
            Assert.Equal(0f, field.Distance, 0.05f);

            // ...and inside the bite, since the footprint is the sphere's cross-section.
            Assert.True(Vector3.Distance(p, target) <= Digging.BiteRadius + 0.05f,
                $"{p} is {Vector3.Distance(p, target):F3} from the target, past the bite");
        }
    }

    [Fact]
    public void BrushRingIsEmptyWhenTheBiteCannotBreakTheSurface()
    {
        var map = SyntheticTerrain.Flat();
        Span<Vector3> ring = new Vector3[16];

        // Well below the surface: the sphere is entirely inside rock, so it opens nothing and there
        // is no footprint to draw. Showing a ring here would promise an effect that will not happen.
        Assert.Equal(0, Digging.ProjectedRing(map, new Vector3(5f, 6f, 5f), Vector3.UnitY, ring));
    }

    [Fact]
    public void BrushRingFollowsASlope()
    {
        var map = SyntheticTerrain.Slope(40f);
        float rise = MathF.Tan(40f * (MathF.PI / 180f));

        // The surface climbs with x, so a ring on it cannot be level — its points must vary in Y.
        var hit = new Vector3(4f, SyntheticTerrain.GroundHeight + rise * 4f, 4f);
        var target = Digging.TargetVoxel(hit, Vector3.Normalize(new Vector3(-rise, 1f, 0f)));

        Span<Vector3> ring = new Vector3[16];
        int count = Digging.ProjectedRing(map, target, Vector3.Normalize(new Vector3(-rise, 1f, 0f)), ring);
        Assert.True(count > 0);

        float lowest = float.MaxValue, highest = float.MinValue;
        foreach (var p in ring) { lowest = MathF.Min(lowest, p.Y); highest = MathF.Max(highest, p.Y); }

        Assert.True(highest - lowest > 0.2f,
            $"ring spans only {highest - lowest:F3} in Y on a 40-degree slope — it is not following the surface");
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
