using System.Diagnostics;
using System.Numerics;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// What one actor's movement costs. This is the single largest main-thread cost in the game: the
/// live profile puts 32 NPCs at 2.0-4.2 ms per server tick in movement alone, and in singleplayer
/// the server tick runs inside the client's frame.
///
///     dotnet test --filter MovementBenchmarks --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public class MovementBenchmarks(ITestOutputHelper output)
{
    static readonly Lazy<ChunkMap> World = new(() =>
    {
        var map = new ChunkMap();
        WorldGen.Generate(map);
        return map;
    });

    static double Us(long ticks, int n) => ticks * 1_000_000.0 / Stopwatch.Frequency / n;

    [Fact]
    public void StepAndItsCollisionQueries()
    {
        var map = World.Value;
        var intent = Vector3.Normalize(new Vector3(1f, 0f, 0.4f));

        // Spread the actors out so the measurement is not one lucky cache-resident column.
        var states = new List<MoveState>();
        for (int i = 0; i < 32; i++)
            states.Add(PlayerMovement.SpawnAt(map, 40f + i * 7f, 40f + i * 5f));

        for (int warm = 0; warm < 200; warm++)
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                PlayerMovement.Step(map, ref s, intent, PlayerStateFlags.Moving, NetworkConfig.FixedDt);
                states[i] = s;
            }

        const int ticks = 300;
        long t0 = Stopwatch.GetTimestamp();
        for (int tick = 0; tick < ticks; tick++)
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                PlayerMovement.Step(map, ref s, intent, PlayerStateFlags.Moving, NetworkConfig.FixedDt);
                states[i] = s;
            }
        double stepUs = Us(Stopwatch.GetTimestamp() - t0, ticks * states.Count);

        // The two queries underneath it, measured on their own.
        var feet = states[0].Position;
        long t1 = Stopwatch.GetTimestamp();
        for (int i = 0; i < 200_000; i++)
            TerrainCollision.TryDeepestContact(map, PlayerMovement.Body, feet, out _);
        double contactUs = Us(Stopwatch.GetTimestamp() - t1, 200_000);

        long t2 = Stopwatch.GetTimestamp();
        for (int i = 0; i < 200_000; i++)
            TerrainCollision.TrySample(map, feet, out _);
        double sampleUs = Us(Stopwatch.GetTimestamp() - t2, 200_000);

        long t3 = Stopwatch.GetTimestamp();
        for (int i = 0; i < 1_000_000; i++)
            TerrainCollision.TrySampleRaw(map, feet, out _);
        double rawUs = Us(Stopwatch.GetTimestamp() - t3, 1_000_000);

        output.WriteLine($"PlayerMovement.Step      {stepUs,8:F2} us   (32 actors -> {stepUs * 32 / 1000:F2} ms/tick)");
        output.WriteLine($"  TryDeepestContact      {contactUs,8:F2} us   ({contactUs / stepUs * 100:F0}% of a step, x{stepUs / contactUs:F1} per step)");
        output.WriteLine($"  TrySample              {sampleUs,8:F2} us   (3 per contact)");
        output.WriteLine($"  TrySampleRaw           {rawUs,8:F2} us   (7 per sample: 1 cell + 6 gradient)");
    }
}
