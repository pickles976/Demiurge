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

    /// <summary>
    /// The same comparison across WORKING SET SIZES, which is the axis the single-chunk benchmark
    /// above cannot vary and therefore cannot answer.
    ///
    /// One chunk is 64 KB interleaved and 32 KB as a bare density plane. Both sit in L2 on any
    /// machine this runs on, so that measurement is bounded by the loads and the address arithmetic
    /// and reports ~1.0x no matter what the layout does to bandwidth. Halving a footprint only pays
    /// where the footprint was the constraint, so the question is at which size the ratio departs
    /// from 1 — and whether the server's real hot set is anywhere near it.
    ///
    /// Reads stay in bursts of 14 within one cell, as collision issues them; only the cell chosen
    /// each burst roams over more chunks. That keeps the access pattern honest while moving the
    /// footprint, rather than replacing a stencil with a random walk and calling it a cache test.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void WorkingSetSweep()
    {
        output.WriteLine($"{Samples:N0} bursts of 14 reads, working set swept over chunk count");
        output.WriteLine("chunks     interleaved      separate     ratio    density plane");

        foreach (int chunks in new[] { 1, 8, 64, 256, 1024 })
        {
            int volume = Volume * chunks;
            var interleaved = new Voxel[volume];
            var density = new sbyte[volume];

            var random = new Random(1234);
            for (int i = 0; i < volume; i++)
            {
                sbyte value = (sbyte)random.Next(-127, 128);
                interleaved[i] = new Voxel { Density = value, Material = BlockType.BlockType_Dirt };
                density[i] = value;
            }

            var cells = new int[Samples];
            for (int i = 0; i < Samples; i++)
            {
                int chunk = random.Next(chunks);
                int x = random.Next(2, Side - 3);
                int y = random.Next(2, Height - 3);
                int z = random.Next(2, Side - 3);
                cells[i] = chunk * Volume + Index(x, y, z);
            }

            long warm = SumInterleaved(interleaved, cells) + SumSeparate(density, cells);
            Assert.NotEqual(long.MinValue, warm);

            var timer = Stopwatch.StartNew();
            long a = SumInterleaved(interleaved, cells);
            double interleavedMs = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            long b = SumSeparate(density, cells);
            double separateMs = timer.Elapsed.TotalMilliseconds;

            Assert.Equal(a, b);

            double planeKb = volume / 1024.0;
            output.WriteLine(
                $"{chunks,6}  {interleavedMs,10:F2} ms  {separateMs,10:F2} ms  {interleavedMs / separateMs,7:F2}x  {planeKb,8:N0} KB");
        }
    }

    /// <summary>
    /// The footprint half of the question, which has nothing to do with cache lines.
    ///
    /// A slab collapses to a single value only when density AND material are both uniform across it.
    /// Separate planes would let each collapse on its own, and the two fields do not go uniform at the
    /// same depth: stored density saturates about 2.54 voxels below the surface, while material keeps
    /// varying down to <see cref="ChunkGenerator.SoilDepth"/> because it is derived from the TRUE
    /// distance. Every slab in that gap currently carries a 512-byte array to record a density value
    /// that is the same everywhere in it.
    ///
    /// This counts them on real generated terrain rather than reasoning about the bands, since the
    /// height range inside a 16x16 chunk smears both boundaries across several slabs.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void SlabUniformityBySeparatePlane()
    {
        int allocated = 0, densityUniform = 0, materialUniform = 0, bothUniform = 0, total = 0;

        for (int cz = 0; cz < 8; cz++)
        {
            for (int cx = 0; cx < 8; cx++)
            {
                var chunk = ChunkGenerator.GenerateChunk(new ChunkIndex { x = cx, z = cz });
                chunk.CollapseUniformSlabs();

                for (int slabY = 0; slabY < ChunkConstants.ChunkHeight; slabY++)
                {
                    total++;
                    if (chunk.IsUniform(slabY)) { bothUniform++; densityUniform++; materialUniform++; continue; }

                    allocated++;

                    var first = chunk[slabY * ChunkConstants.ChunkSize];
                    bool sameDensity = true, sameMaterial = true;

                    for (int i = 1; i < ChunkConstants.ChunkSize; i++)
                    {
                        var voxel = chunk[slabY * ChunkConstants.ChunkSize + i];
                        if (voxel.Density != first.Density) sameDensity = false;
                        if (voxel.Material != first.Material) sameMaterial = false;
                        if (!sameDensity && !sameMaterial) break;
                    }

                    if (sameDensity) densityUniform++;
                    if (sameMaterial) materialUniform++;
                }
            }
        }

        // Interleaved: a mixed slab costs 2 bytes per voxel. Separate: each plane pays 1 byte per
        // voxel only if that plane is actually mixed.
        long interleavedVoxels = allocated * (long)ChunkConstants.ChunkSize * 2;
        int separateSlabs = (total - densityUniform) + (total - materialUniform);
        long separateVoxels = separateSlabs * (long)ChunkConstants.ChunkSize;

        // The bookkeeping the voxel arithmetic above ignores, and which decides the answer.
        // Per chunk today: one Voxel[]?[128] of references (1 KB) and one Voxel[128] of uniform
        // values (256 B). Split in two, the reference array is paid TWICE — every chunk in the map,
        // whether or not any of its slabs benefited. Each allocated array also carries an object
        // header, and separate planes allocate more arrays than interleaved does.
        const int Chunks = 64;
        const int Header = 24;
        long interleavedOverhead = Chunks * (128L * 8 + 128L * 2) + allocated * (long)Header;
        long separateOverhead = Chunks * (2 * 128L * 8 + 2 * 128L) + separateSlabs * (long)Header;

        long interleavedTotal = interleavedVoxels + interleavedOverhead;
        long separateTotal = separateVoxels + separateOverhead;

        output.WriteLine($"{Chunks} chunks, {total:N0} slabs");
        output.WriteLine($"uniform in both fields (collapsed today) {bothUniform,8:N0}");
        output.WriteLine($"allocated today                          {allocated,8:N0}");
        output.WriteLine($"  ... of those, density-uniform          {densityUniform - bothUniform,8:N0}");
        output.WriteLine($"  ... of those, material-uniform         {materialUniform - bothUniform,8:N0}");
        output.WriteLine($"arrays allocated, separate planes        {separateSlabs,8:N0}");
        output.WriteLine("");
        output.WriteLine($"voxel bytes    interleaved {interleavedVoxels,9:N0}   separate {separateVoxels,9:N0}");
        output.WriteLine($"bookkeeping    interleaved {interleavedOverhead,9:N0}   separate {separateOverhead,9:N0}");
        output.WriteLine($"TOTAL          interleaved {interleavedTotal,9:N0}   separate {separateTotal,9:N0}");
        output.WriteLine($"voxel-only saving  {100.0 * (interleavedVoxels - separateVoxels) / interleavedVoxels,6:F1}%");
        output.WriteLine($"real saving        {100.0 * (interleavedTotal - separateTotal) / interleavedTotal,6:F1}%");
    }
}
