using System.Diagnostics;
using System.Numerics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// What ONE dig costs, broken into the pieces that happen per edit, so "chunk changes are slow when
/// lots of NPCs dig" becomes a number per stage instead of a guess about which stage.
///
/// Run with output visible:
///     dotnet test --filter DigBenchmarks --logger "console;verbosity=detailed"
///
/// The load to compare against: TicksPerDig is 15 at 30 Hz, so every digging actor asks for two
/// edits a second, and every client replays every accepted edit. Thirty-two NPCs digging is
/// therefore ~64 edits/second arriving at one main thread.
/// </summary>
[Trait("Category", "Benchmark")]
public class DigBenchmarks(ITestOutputHelper output)
{
    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    static readonly Lazy<ChunkMap> World = new(() =>
    {
        var map = new ChunkMap();
        WorldGen.Generate(map);
        return map;
    });

    /// <summary>Digs down a column the way an NPC excavating a foxhole does, timing each stage.</summary>
    [Fact]
    public void OneDig()
    {
        var map = World.Value;
        var scratch = new Sample[ChunkMesher.ScratchVolume];
        var sections = new List<SectionIndex>();

        // Somewhere with real terrain in it rather than the map edge.
        const int digX = 8, digZ = 8;
        float surface = SurfaceQuery.HighestSurfaceY(map, digX, digZ) ?? 40f;

        // Warm up: JIT the edit and mesh paths before anything is timed.
        for (int i = 0; i < 20; i++)
            TerrainEdits.ApplyBox(
                map, new Vector3(digX + 40, surface, digZ + 40), Digging.Bite,
                EditMode.SubtractSoil, BlockType.BlockType_Air, EditShape.Sphere, Digging.BiteStrength);

        const int digs = 200;
        long edit = 0, collect = 0, fill = 0, contour = 0, crease = 0;
        int sectionsDirtied = 0, meshed = 0;

        for (int i = 0; i < digs; i++)
        {
            // Spread the digs out so each one lands on untouched ground, as separate NPCs would.
            var centre = new Vector3(digX + i % 20 * 3, surface - i / 20, digZ + i % 7 * 3);

            long t0 = Stopwatch.GetTimestamp();
            var (min, max) = TerrainEdits.ApplyBox(
                map, centre, Digging.Bite,
                EditMode.SubtractSoil, BlockType.BlockType_Air, EditShape.Sphere, Digging.BiteStrength);
            long t1 = Stopwatch.GetTimestamp();
            edit += t1 - t0;

            sections.Clear();
            ChunkMesher.CollectDependentSections(
                (int)MathF.Floor(min.X), (int)MathF.Floor(min.Y), (int)MathF.Floor(min.Z),
                (int)MathF.Ceiling(max.X), (int)MathF.Ceiling(max.Y), (int)MathF.Ceiling(max.Z),
                sections);
            collect += Stopwatch.GetTimestamp() - t1;
            sectionsDirtied += sections.Count;

            foreach (var section in sections)
            {
                long t2 = Stopwatch.GetTimestamp();
                bool ok = ChunkMesher.TryFillScratch(map, section, scratch);
                long t3 = Stopwatch.GetTimestamp();
                fill += t3 - t2;
                if (!ok) continue;

                var mesh = ChunkMesher.GenerateMesh(scratch);
                long t4 = Stopwatch.GetTimestamp();
                contour += t4 - t3;
                if (mesh.Indices.Length == 0) continue;

                ChunkMesher.SplitCreases(mesh);
                crease += Stopwatch.GetTimestamp() - t4;
                meshed++;
            }
        }

        double perDigEdit = Ms(edit) / digs;
        double perDigCollect = Ms(collect) / digs;
        double perDigMesh = (Ms(fill) + Ms(contour) + Ms(crease)) / digs;

        output.WriteLine($"{digs} digs, {sectionsDirtied} sections dirtied "
                       + $"({sectionsDirtied / (double)digs:F1} per dig), {meshed} with geometry");
        output.WriteLine("");
        output.WriteLine($"  ApplyBox (field CSG)      {perDigEdit,7:F3} ms/dig   <- server tick AND every client");
        output.WriteLine($"  CollectDependentSections  {perDigCollect,7:F3} ms/dig");
        output.WriteLine($"  TryFillScratch            {Ms(fill) / digs,7:F3} ms/dig   <- worker threads");
        output.WriteLine($"  GenerateMesh              {Ms(contour) / digs,7:F3} ms/dig   <- worker threads");
        output.WriteLine($"  SplitCreases              {Ms(crease) / digs,7:F3} ms/dig   <- worker threads");
        output.WriteLine("");
        output.WriteLine($"  main thread per dig       {perDigEdit + perDigCollect,7:F3} ms");
        output.WriteLine($"  worker time per dig       {perDigMesh,7:F3} ms");
        output.WriteLine("");
        foreach (int diggers in new[] { 1, 8, 16, 32 })
        {
            double editsPerSecond = diggers * (NetworkConfig.TickRate / (double)Digging.TicksPerDig);
            output.WriteLine(
                $"  {diggers,2} diggers = {editsPerSecond,4:F0} edits/s -> "
              + $"main {editsPerSecond * (perDigEdit + perDigCollect),6:F1} ms/s, "
              + $"workers {editsPerSecond * perDigMesh,7:F1} ms/s "
              + $"({editsPerSecond * perDigMesh / 1000.0 / 8:P0} of 8 workers)");
        }
    }

    /// <summary>
    /// The stage the whole thing is suspected on: how much of one dig's cost is the tiny-component
    /// cull, which runs a flood fill over the affected box after every spherical subtraction.
    /// </summary>
    [Fact]
    public void CullShare()
    {
        var map = World.Value;
        const int digX = 300, digZ = 300;
        float surface = SurfaceQuery.HighestSurfaceY(map, digX, digZ) ?? 40f;

        var (low, high) = TerrainEdits.AffectedBounds(
            new Vector3(digX, surface, digZ), Digging.Bite);
        output.WriteLine($"Dig brush radius {Digging.BiteRadius}, affected box "
                       + $"{high.X - low.X + 1}x{high.Y - low.Y + 1}x{high.Z - low.Z + 1} voxels");

        for (int i = 0; i < 20; i++)
            TerrainEdits.CullTinySolidComponents(map, low, high, 4);

        const int reps = 2000;
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < reps; i++)
            TerrainEdits.CullTinySolidComponents(map, low, high, 4);
        double cull = Ms(Stopwatch.GetTimestamp() - t0) / reps;

        output.WriteLine($"  CullTinySolidComponents   {cull,7:F3} ms/dig");
    }
}
