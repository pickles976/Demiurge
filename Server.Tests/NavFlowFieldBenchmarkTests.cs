using System.Diagnostics;
using System.Numerics;
using Demiurge.GameServer;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// What a 500 m flow field costs to build on the real conquest map, and how much of the map it
/// covers.
///
/// This is the feasibility question for computing fields at game start, and it is not answerable by
/// arithmetic: a solve visits each cell once, but an expansion here runs the same standability and
/// step checks A* pays, which measured at roughly 0.8 ms of CPU each in the live scenario. Whether
/// four fields cost seconds or minutes decides whether they are built on load, baked into the map,
/// or streamed in behind the opening approach.
/// </summary>
public sealed class NavFlowFieldBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void FieldsForEveryConquestFlag()
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(
            Path.Combine(root, "maps", "conquest", "runtime.dmap"));
        var flags = map.Placements
            .Where(placement => placement.Kind == RuntimePlacementKind.Flag)
            .ToArray();
        Assert.NotEmpty(flags);

        // Deliberately NOT sharing a NavStandabilityCache across these solves. Measured: it cost
        // 88.9 s -> 116.7 s at a 17% hit rate, because a Dijkstra sweep settles each cell once and
        // has almost nothing to re-ask, while every miss still pays the shared lookup and its
        // revision check. The second tier is for repeated-query workloads, not for this one.
        long totalCells = 0;
        var wholeRun = Stopwatch.StartNew();
        foreach (var flag in flags)
        {
            Assert.True(
                NavTraversal.TryFindNearestStandable(
                    map.Terrain,
                    flag.Position,
                    horizontalRadius: 8,
                    out var goal),
                $"flag at {flag.Position} has no standable cell");

            var timer = Stopwatch.StartNew();
            var field = NavFlowField.Build(map.Terrain, goal);
            timer.Stop();
            totalCells += field.Count;

            output.WriteLine(
                $"flag ({flag.Position.X:0}, {flag.Position.Z:0}): "
                + $"{field.Count:N0} cells, {field.Expanded:N0} expanded, "
                + $"{timer.Elapsed.TotalSeconds:0.00} s, "
                + $"{(field.Complete ? "complete" : "PARTIAL")}, "
                + $"{timer.Elapsed.TotalMilliseconds * 1000d / Math.Max(1, field.Expanded):0} us/cell");
        }
        wholeRun.Stop();

        // 12 bytes of payload per cell (float cost + long successor) before container overhead; the
        // dictionary itself costs several times that, which is what the follow-up storage work is
        // for if this proves worth keeping resident.
        output.WriteLine(
            $"all {flags.Length} fields: {wholeRun.Elapsed.TotalSeconds:0.0} s, "
            + $"{totalCells:N0} cells, ~{totalCells * 12 / 1024 / 1024:N0} MB of payload");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("repository root not found");
    }
}
