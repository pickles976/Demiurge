using System.Diagnostics;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// What a line-of-sight ray costs.
/// </summary>
/// <remarks>
/// This is the shared root of the two AI costs that blow the server tick apart during combat. A live
/// conquest profile put <c>perception</c> at 21.7 ms/tick and <c>cover</c> at 24.1 ms/tick against a
/// 33 ms budget, and both bottom out here — <c>Perception</c> casts one or two rays per NPC per tick,
/// and one <c>CoverBehavior</c> query evaluates thirteen candidate positions against up to two threats.
/// <para>
/// The cover budget is already <c>CoverQueriesPerTick = 1</c>, so it cannot be throttled further. The
/// ray itself has to get cheaper.
/// </para>
///
///     dotnet test --filter RaycastBenchmarks --logger "console;verbosity=detailed"
/// </remarks>
[Trait("Category", "Benchmark")]
public class RaycastBenchmarks(ITestOutputHelper output)
{
    static readonly Lazy<ChunkMap> World = new(() =>
    {
        var map = new ChunkMap();
        WorldGen.Generate(map);
        return map;
    });

    static double Us(long ticks, int n) => ticks * 1_000_000.0 / Stopwatch.Frequency / n;

    /// <summary>Rays shaped like the ones perception and cover actually cast: eye height, mostly
    /// horizontal, out to combat range, spread across terrain rather than one lucky column.</summary>
    static List<(Vector3 Origin, Vector3 Direction)> CombatRays(ChunkMap map, int count)
    {
        var rays = new List<(Vector3, Vector3)>(count);
        var rng = new Random(12345);

        for (int i = 0; i < count; i++)
        {
            var from = PlayerMovement.SpawnAt(map, 60f + i % 37 * 9f, 60f + i % 23 * 11f);
            var origin = from.Position + Vector3.UnitY * Digging.EyeHeight;

            double angle = rng.NextDouble() * Math.Tau;
            float pitch = (float)(rng.NextDouble() * 0.3 - 0.15);
            var direction = Vector3.Normalize(new Vector3(
                (float)Math.Cos(angle),
                pitch,
                (float)Math.Sin(angle)));

            rays.Add((origin, direction));
        }

        return rays;
    }

    [Fact]
    public void LineOfSightRayCost()
    {
        var map = World.Value;
        const float range = 60f;
        var rays = CombatRays(map, 512);

        for (int warm = 0; warm < 2; warm++)
            foreach (var (origin, direction) in rays)
                _ = TerrainRaycast.Cast(map, origin, direction, range);

        int hits = 0;
        var clock = Stopwatch.StartNew();
        const int passes = 8;
        for (int pass = 0; pass < passes; pass++)
            foreach (var (origin, direction) in rays)
                if (TerrainRaycast.Cast(map, origin, direction, range) is not null)
                    hits++;
        clock.Stop();

        int total = rays.Count * passes;
        double perRay = Us(clock.ElapsedTicks, total);

        output.WriteLine($"{total} rays at {range} m: {perRay:0.00} us/ray ({hits} hits)");

        // What the live profile implies these add up to.
        output.WriteLine($"  perception, 32 agents x 1 ray:      {perRay * 32 / 1000.0:0.00} ms/tick");
        output.WriteLine($"  one cover query, ~13 candidates x 2 threats x 2 stances: "
                         + $"{perRay * 52 / 1000.0:0.00} ms");

        Assert.True(perRay > 0);
    }
}
