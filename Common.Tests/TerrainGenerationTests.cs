using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Terrain generation: the height budget, that plains and mountains actually differ, the padded column
/// indexing slope depends on, and steep faces coming out as rock.
///
/// These are property tests, not golden values. Nobody should have to update an expected height when a
/// spline gets retuned — but "the world stays inside the world" and "plains are flatter than mountains"
/// must hold across any retune, and those are what break silently.
/// </summary>
public class TerrainGenerationTests
{
    // ---- Height budget ----

    /// <summary>
    /// Terrain has to fit the world. Above WorldMaxY it is simply cut off, and near WorldMinY it hits the
    /// bedrock clamp — both look like generation bugs rather than like a budget being exceeded.
    /// </summary>
    [Fact]
    public void HeightBudgetFitsInsideTheWorld()
    {
        Assert.True(TerrainShape.HighestPossible < ChunkConstants.WorldMaxY - 8,
            $"peaks can reach {TerrainShape.HighestPossible} against a world top of {ChunkConstants.WorldMaxY}");

        Assert.True(TerrainShape.LowestPossible > ChunkConstants.WorldMinY + ChunkConstants.BedrockThickness + 4,
            $"valleys can reach {TerrainShape.LowestPossible} against a floor of {ChunkConstants.WorldMinY}");
    }

    /// <summary>
    /// The world should use a decent share of the 128 available, or the mountains are not mountains. The
    /// old generator spanned 16 voxels of the column, which is what made everything look like gentle
    /// hills whatever the noise did.
    /// </summary>
    [Fact]
    public void HeightBudgetActuallySpendsTheColumn()
        => Assert.True(TerrainShape.HighestPossible - TerrainShape.LowestPossible > 50f,
            $"only {TerrainShape.HighestPossible - TerrainShape.LowestPossible} voxels of relief across the whole range");

    // ---- Plains vs mountains ----

    /// <summary>
    /// The point of the whole exercise: erosion has to produce two visibly different kinds of terrain
    /// from one formula. Compares the spread of heights that each erosion regime can produce across the
    /// full sweep of detail and ridge.
    /// </summary>
    [Fact]
    public void ErosionSeparatesFlatRegionsFromMountainousOnes()
    {
        (float min, float max) Range(float erosion)
        {
            float min = float.MaxValue, max = float.MinValue;

            for (int p = 0; p <= 10; p++)
                for (int d = -10; d <= 10; d++)
                    for (int r = 0; r <= 10; r++)
                    {
                        float h = TerrainShape.Height(erosion, p / 10f, d / 10f, r / 10f);
                        min = MathF.Min(min, h);
                        max = MathF.Max(max, h);
                    }

            return (min, max);
        }

        var plains = Range(0.6f);      // deep in the eroded shelf
        var mountains = Range(-0.8f);

        float plainsRelief = plains.max - plains.min;
        float mountainRelief = mountains.max - mountains.min;

        Assert.True(plainsRelief < 3f, $"plains are not flat: {plainsRelief} voxels of relief");
        Assert.True(mountainRelief > 20f, $"mountains are not mountainous: {mountainRelief} voxels of relief");
        Assert.True(mountains.min > plains.max, "mountains must sit above plains, not merely be rougher");
    }

    /// <summary>
    /// The shelf: across the whole eroded half, plains sit at one altitude.
    ///
    /// Measured OFF a ridgeline (pv = 0), which is what "plains are flat" means now that chains carry a
    /// low tail into eroded ground. A spur running through plains is wanted — a range should not stop
    /// dead at an erosion boundary — but the ground either side of it must still be flat, and a spur must
    /// stay a spur rather than becoming a mountain.
    /// </summary>
    [Fact]
    public void ErodedHalfIsNearlyOneAltitude()
    {
        float min = float.MaxValue, max = float.MinValue;

        for (int e = 3; e <= 10; e++)      // erosion 0.3 .. 1.0
        {
            float h = TerrainShape.Height(e / 10f, pv: 0f, detail: 0f, ridge: 0f);
            min = MathF.Min(min, h);
            max = MathF.Max(max, h);
        }

        Assert.True(max - min < 4f, $"the eroded shelf spans {max - min} voxels; it should be nearly flat");

        // And a chain crossing plains stays a spur.
        float spur = TerrainShape.Height(0.5f, pv: 1f, detail: 0f, ridge: 0f)
                   - TerrainShape.Height(0.5f, pv: 0f, detail: 0f, ridge: 0f);

        Assert.InRange(spur, 0.5f, 8f);
    }

    /// <summary>Ridges must only appear in mountains, or plains grow spikes.</summary>
    [Fact]
    public void RidgesDoNotReachThePlains()
    {
        float withRidge = TerrainShape.Height(0.5f, pv: 1f, detail: 0f, ridge: 1f);
        float withoutRidge = TerrainShape.Height(0.5f, pv: 1f, detail: 0f, ridge: 0f);

        Assert.Equal(withoutRidge, withRidge, 3);

        // ...and they must reach the mountains, or folding the noise bought nothing.
        Assert.True(TerrainShape.Height(-0.8f, 1f, 0f, 1f) - TerrainShape.Height(-0.8f, 1f, 0f, 0f) > 8f,
            "ridges contribute nothing to mountains");
    }

    /// <summary>
    /// FOOTHILLS. A meaningful share of the erosion range has to land at intermediate elevation, or
    /// mountains rise straight out of flat plains and read as blobs sitting on the ground rather than as
    /// ranges belonging to the landscape.
    ///
    /// This is a real regression, not a hypothetical: compressing the transition to make rises dramatic
    /// and widening the plains shelf at the same time deleted the middle of the curve entirely. Both
    /// changes were individually reasonable and together they removed a feature nobody was looking at.
    /// </summary>
    [Fact]
    public void ThereIsAGradedBandBetweenPlainsAndMountains()
    {
        float plains = TerrainShape.Height(1f, 1f, 0f, 0f);
        float peak = TerrainShape.Height(-1f, 1f, 0f, 0f);

        // The middle third of the elevation range, which is what "foothills" means here.
        float low = plains + (peak - plains) * 0.25f;
        float high = plains + (peak - plains) * 0.75f;

        int middle = 0, total = 0;

        for (float erosion = -1f; erosion <= 1f; erosion += 0.01f)
        {
            float height = TerrainShape.Height(erosion, pv: 1f, detail: 0f, ridge: 0f);
            total++;

            if (height > low && height < high) middle++;
        }

        Assert.True(middle > total * 0.12f,
            $"only {middle * 100.0 / total:F0}% of the erosion range lands between {low:F0} and {high:F0} — "
          + "mountains will look like blobs on flat ground");
    }

    /// <summary>Foothills need their own bumpiness, or the graded band is a smooth ramp rather than hills.</summary>
    [Fact]
    public void FoothillsAreNotSmooth()
    {
        float relief = TerrainShape.Height(-0.2f, pv: 1f, detail: 1f, ridge: 0f)
                     - TerrainShape.Height(-0.2f, pv: 1f, detail: -1f, ridge: 0f);

        Assert.True(relief > 6f, $"mid-erosion ground only moves {relief:F1} voxels; it will read as a ramp");
    }

    // ---- The noise actually driving it ----

    /// <summary>
    /// The splines are drawn over [-1, 1]. If the noise only ever produces a narrow band around zero,
    /// most of every curve is dead and the world silently collapses to one altitude — which is exactly
    /// what happens if the fractal helper's unnormalised output is not divided back down, or if it is
    /// divided twice. So the field's actual range is worth asserting rather than assuming.
    /// </summary>
    [Fact]
    public void GeneratedHeightsSpanTheSplineDomain()
    {
        float min = float.MaxValue, max = float.MinValue;

        // A wide sweep of the real world, sampling chunk by chunk.
        for (int cx = -20; cx <= 20; cx += 4)
            for (int cz = -20; cz <= 20; cz += 4)
            {
                float[] heights = NoiseGen.GenerateHeightsForChunk(new ChunkIndex { x = cx, z = cz });

                foreach (float h in heights)
                {
                    min = MathF.Min(min, h);
                    max = MathF.Max(max, h);
                }
            }

        Assert.True(min > ChunkConstants.WorldMinY + ChunkConstants.BedrockThickness,
            $"generated a height of {min}, at or below bedrock");
        Assert.True(max < ChunkConstants.WorldMaxY,
            $"generated a height of {max}, past the top of the world");

        // The real assertion: the world has both low and high ground in it.
        Assert.True(max - min > 30f,
            $"heights only span {max - min} voxels ({min}..{max}) — the erosion spline is barely being used");
    }

    // ---- Padded column indexing ----

    /// <summary>
    /// The padded ring is only there so slope can be differenced at a chunk edge, and a padded-vs-
    /// unpadded index mixup is the exact bug class the coordinate rule exists to prevent. So the two
    /// index paths are pinned against each other.
    /// </summary>
    [Fact]
    public void PaddedColumnIndexAgreesWithTheVoxelPath()
    {
        for (int z = 0; z < ChunkConstants.ChunkWidth; z++)
            for (int x = 0; x < ChunkConstants.ChunkWidth; x++)
            {
                int voxel = ChunkTransforms.LocalVoxelIndex(x, 0, z);

                Assert.Equal(ChunkTransforms.PaddedColumnIndex(x, z),
                             ChunkTransforms.PaddedColumnIndexOf(voxel));

                // And the same voxel higher up the column maps to the same column.
                int higher = ChunkTransforms.LocalVoxelIndex(x, 37, z);
                Assert.Equal(ChunkTransforms.PaddedColumnIndex(x, z),
                             ChunkTransforms.PaddedColumnIndexOf(higher));
            }
    }

    /// <summary>Padded slots must map to the world positions they claim, including the -1 ring.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, -3)]
    [InlineData(-20, 20)]
    public void PaddedWorldPositionsRingTheChunk(int chunkX, int chunkZ)
    {
        var index = new ChunkIndex { x = chunkX, z = chunkZ };
        (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);

        for (int z = -1; z <= ChunkConstants.ChunkWidth; z++)
            for (int x = -1; x <= ChunkConstants.ChunkWidth; x++)
            {
                var position = ChunkTransforms.PaddedColumnWorldPosition(
                    index, ChunkTransforms.PaddedColumnIndex(x, z));

                Assert.Equal(new Vector2(originX + x, originZ + z), position);
            }
    }

    /// <summary>
    /// The interior of a padded array must agree with the unpadded world positions, or slope is
    /// differenced against the wrong columns and cliffs appear one voxel off from where they are.
    /// </summary>
    [Fact]
    public void PaddedInteriorMatchesTheUnpaddedColumnPositions()
    {
        var index = new ChunkIndex { x = -2, z = 5 };

        for (int z = 0; z < ChunkConstants.ChunkWidth; z++)
            for (int x = 0; x < ChunkConstants.ChunkWidth; x++)
            {
                var unpadded = ChunkTransforms.ColumnWorldPosition(index, ChunkTransforms.ColumnIndex(x, z));
                var padded = ChunkTransforms.PaddedColumnWorldPosition(
                    index, ChunkTransforms.PaddedColumnIndex(x, z));

                Assert.Equal(unpadded, padded);
            }
    }

    // ---- Steep faces are rock ----

    [Fact]
    public void GentleGroundIsGrassAndSteepGroundIsStone()
    {
        // The topmost solid voxel, in stored terms — the band DensityToMaterial calls Grass.
        const float Surface = -0.5f;

        float gentle = MathF.Tan((ChunkGenerator.GrassLimitDegrees - 15f) * (MathF.PI / 180f));
        float steep = MathF.Tan((ChunkGenerator.GrassLimitDegrees + 15f) * (MathF.PI / 180f));

        Assert.Equal(BlockType.BlockType_Grass, ChunkGenerator.DensityToMaterial(Surface, Surface, gentle));
        Assert.Equal(BlockType.BlockType_Stone, ChunkGenerator.DensityToMaterial(Surface, Surface, steep));
    }

    [Fact]
    public void StoneSlopeThresholdMatchesTheWalkableSlopeLimit()
        => Assert.Equal(PlayerMovement.MaxSlopeDegrees, ChunkGenerator.GrassLimitDegrees);

    /// <summary>A steep column is rock all the way down, not a stripe of grass over dirt.</summary>
    [Fact]
    public void SteepColumnsAreStoneAtEveryDepth()
    {
        float steep = ChunkGenerator.GrassLimitSlope * 2f;

        foreach (float depth in new[] { 0.5f, 1.5f, 3f, 12f })
            Assert.Equal(BlockType.BlockType_Stone,
                ChunkGenerator.DensityToMaterial(-MathF.Min(depth, 2.5f), -depth, steep));
    }

    /// <summary>Slope never overrides air. Material is meaningless there and the invariant comes first.</summary>
    [Fact]
    public void SlopeDoesNotTurnAirIntoStone()
        => Assert.Equal(BlockType.BlockType_Air,
            ChunkGenerator.DensityToMaterial(0.5f, 0.5f, ChunkGenerator.GrassLimitSlope * 4f));

    /// <summary>
    /// Grass has to survive somewhere on real terrain, and stone has to appear somewhere. Either
    /// extreme — an all-grass world or an all-rock one — means the threshold is on the wrong side of
    /// what the noise actually produces, which is a whole feature silently doing nothing.
    ///
    /// Samples across the WHOLE map, not a patch near the origin: erosion's wavelength is 220 voxels, so
    /// a small sample can sit entirely inside one regime and see only plains. That is how this test
    /// first passed the grass half and failed the stone half.
    /// </summary>
    [Fact]
    public void RealTerrainHasBothGrassAndExposedStone()
    {
        int grass = 0, stone = 0;

        for (int cx = -20; cx <= 20; cx += 8)
            for (int cz = -20; cz <= 20; cz += 8)
            {
                var chunk = ChunkGenerator.GenerateChunk(new ChunkIndex { x = cx, z = cz });

                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    // Only the surface layer says anything about slope; deep voxels are stone regardless.
                    if (chunk[i].Distance is >= 0f or < -1f) continue;

                    if (chunk[i].Material == BlockType.BlockType_Grass) grass++;
                    if (chunk[i].Material == BlockType.BlockType_Stone) stone++;
                }
            }

        Assert.True(grass > 0, "no grass anywhere: the slope threshold is below what the terrain produces");
        Assert.True(stone > 0, "no exposed stone anywhere: nothing is steep enough to trip the threshold");
    }
}
