using System.Diagnostics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// What separating density from material would actually buy, measured before anything is refactored.
///
/// The premise is sound on paper: <see cref="Voxel"/> is two bytes — an sbyte of quantized distance
/// and a <see cref="BlockType"/> — and the server never reads the material. Collision, raycasting and
/// standability are density-only, so every 64-byte cache line fetched delivers 32 useful bytes, which
/// is exactly the "cache-hostile access costs twice" problem the reference machine has with its
/// shared DDR4.
///
/// The premise is also easy to be wrong about. A chunk's density plane is 32 KB and its slabs are
/// lazily allocated, so a hot working set may already sit in L2 where halving it changes nothing —
/// and this codebase has already had one layout change (the 3x3x3 block fetch) measure 2x WORSE than
/// the scattered reads it replaced. So this reproduces the access pattern that dominates the server —
/// a trilinear sample's eight corners, plus the six-point central-difference stencil around them —
/// against both layouts, and reports the ratio.
/// </summary>
public class VoxelLayoutBenchmarks(ITestOutputHelper output)
{
    private const int Side = 16;
    private const int Height = 128;
    private const int Volume = Side * Side * Height;
    private const int Samples = 200_000;

    [Fact]
    [Trait("Category", "Benchmark")]
    public void InterleavedAgainstSeparatePlanes()
    {
        var interleaved = new Voxel[Volume];
        var density = new sbyte[Volume];
        var material = new BlockType[Volume];
        var random = new Random(1234);
        for (int i = 0; i < Volume; i++)
        {
            sbyte value = (sbyte)random.Next(-127, 128);
            interleaved[i] = new Voxel { Density = value, Material = BlockType.BlockType_Dirt };
            density[i] = value;
            material[i] = BlockType.BlockType_Dirt;
        }

        // The same pseudo-random cell sequence for both, so the comparison is layout and nothing else.
        var cells = new int[Samples];
        for (int i = 0; i < Samples; i++)
        {
            // Two clear on every side: the stencil reaches +2 cells and +2 slabs from the corner.
            int x = random.Next(2, Side - 3);
            int y = random.Next(2, Height - 3);
            int z = random.Next(2, Side - 3);
            cells[i] = Index(x, y, z);
        }

        // Warm both paths before timing; the first pass pays for page faults either way.
        long warm = SumInterleaved(interleaved, cells) + SumSeparate(density, cells);
        Assert.NotEqual(long.MinValue, warm);

        var timer = Stopwatch.StartNew();
        long a = SumInterleaved(interleaved, cells);
        double interleavedMs = timer.Elapsed.TotalMilliseconds;

        timer.Restart();
        long b = SumSeparate(density, cells);
        double separateMs = timer.Elapsed.TotalMilliseconds;

        // Both layouts must agree, or the benchmark is measuring two different computations.
        Assert.Equal(a, b);
        Assert.Equal(Volume, material.Length);

        output.WriteLine($"{Samples:N0} samples of 14 reads each ({Samples * 14L:N0} voxel reads)");
        output.WriteLine($"interleaved (2-byte Voxel)  {interleavedMs,8:F2} ms");
        output.WriteLine($"separate density plane      {separateMs,8:F2} ms");
        output.WriteLine($"ratio                       {interleavedMs / separateMs,8:F2}x");
    }

    /// <summary>Eight trilinear corners plus a six-point gradient stencil: what TrySample reads.</summary>
    private static long SumInterleaved(Voxel[] voxels, int[] cells)
    {
        long sum = 0;
        foreach (int index in cells)
        {
            sum += voxels[index].Density;
            sum += voxels[index + 1].Density;
            sum += voxels[index + Side].Density;
            sum += voxels[index + Side + 1].Density;
            sum += voxels[index + Side * Side].Density;
            sum += voxels[index + Side * Side + 1].Density;
            sum += voxels[index + Side * Side + Side].Density;
            sum += voxels[index + Side * Side + Side + 1].Density;
            sum += voxels[index - 1].Density;
            sum += voxels[index - Side].Density;
            sum += voxels[index - Side * Side].Density;
            sum += voxels[index + 2].Density;
            sum += voxels[index + Side * 2].Density;
            sum += voxels[index + Side * Side * 2].Density;
        }
        return sum;
    }

    private static long SumSeparate(sbyte[] density, int[] cells)
    {
        long sum = 0;
        foreach (int index in cells)
        {
            sum += density[index];
            sum += density[index + 1];
            sum += density[index + Side];
            sum += density[index + Side + 1];
            sum += density[index + Side * Side];
            sum += density[index + Side * Side + 1];
            sum += density[index + Side * Side + Side];
            sum += density[index + Side * Side + Side + 1];
            sum += density[index - 1];
            sum += density[index - Side];
            sum += density[index - Side * Side];
            sum += density[index + 2];
            sum += density[index + Side * 2];
            sum += density[index + Side * Side * 2];
        }
        return sum;
    }

    private static int Index(int x, int y, int z) => y * Side * Side + z * Side + x;
}
