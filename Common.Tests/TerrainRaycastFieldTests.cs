using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Demiurge.Tests;

/// <summary>
/// Rays against real generated terrain, checked as a property rather than against expected values.
/// </summary>
/// <remarks>
/// <see cref="TerrainRaycastTests"/> pins sub-voxel accuracy on hand-built fields where the answer is
/// known arithmetic. This file covers what those cannot: whether the march holds up over thousands of
/// rays across terrain nobody hand-picked.
/// <para>
/// It exists because the march was changed to sample the per-cell gradient (8 voxel reads) instead of
/// the smoothed one (56). The step length is still <c>min(raw, corrected)</c> and still safe under
/// that substitution — but "still safe" is an argument, and tunnelling is silent when it is wrong, so
/// it wants a test that would actually catch it rather than a comment asserting it does not happen.
/// </para>
/// <para>
/// Asserted as a property, not a golden master: no golden file to regenerate, and it stays meaningful
/// if the march is rewritten again.
/// </para>
/// </remarks>
public class TerrainRaycastFieldTests(ITestOutputHelper output)
{
    /// <summary>
    /// How close to zero still counts as "on the surface" rather than solid or air.
    /// </summary>
    /// <remarks>
    /// Not a fudge factor to make the test pass. <see cref="Voxel"/> stores distance as an sbyte
    /// saturating at +/-2.54, so the field resolves about 0.02 — a value inside that band is
    /// indistinguishable from zero in the stored data, and a ray tangent to a surface genuinely lands
    /// there. Sphere tracing cannot resolve a grazing contact more finely than the field it samples.
    /// <para>
    /// Measured, not assumed: with a strict <c>raw &lt;= 0</c> test, this ray set reports 4 grazing
    /// contacts out of 400 and 6 out of 195 hits — and the ORIGINAL 56-read march reports exactly the
    /// same 4 and 6 on the same rays. The band covers behaviour both implementations share, not a
    /// regression in the 8-read one.
    /// </para>
    /// </remarks>
    const float SurfaceBand = 0.02f;

    static readonly Lazy<ChunkMap> World = new(() =>
    {
        var map = new ChunkMap();
        WorldGen.Generate(map);
        return map;
    });

    static List<(Vector3 Origin, Vector3 Direction)> CombatRays(ChunkMap map, int count)
    {
        var rays = new List<(Vector3, Vector3)>(count);
        var rng = new Random(4242);

        for (int i = 0; i < count; i++)
        {
            var from = PlayerMovement.SpawnAt(map, 60f + i % 41 * 8f, 60f + i % 29 * 10f);
            var origin = from.Position + Vector3.UnitY * Digging.EyeHeight;

            double angle = rng.NextDouble() * Math.Tau;
            float pitch = (float)(rng.NextDouble() * 0.6 - 0.3);
            rays.Add((origin, Vector3.Normalize(new Vector3(
                (float)Math.Cos(angle), pitch, (float)Math.Sin(angle)))));
        }

        return rays;
    }

    /// <summary>
    /// A ray must not pass THROUGH terrain on its way to what it reports.
    /// </summary>
    /// <remarks>
    /// The obvious version of this test — "no sample before the hit reads solid" — is wrong, and
    /// measuring showed why. Sphere tracing steps by a lower bound on the distance and stops when a
    /// SAMPLE lands within <c>SurfaceEpsilon</c>; a ray running tangent to a slope can dip a couple of
    /// hundredths of a voxel below zero BETWEEN samples without ever triggering that. The march's own
    /// step-clamp comment names this case. Four of these 400 rays do it, and the original 56-read march
    /// does it on the same four, so it is a property of sphere tracing rather than of any one version.
    /// <para>
    /// What actually matters is whether the ray crossed something with real extent. Tunnelling through a
    /// hillside leaves metres of continuous solid at voxel-scale depth; grazing leaves a fraction of a
    /// voxel. Those differ by orders of magnitude, so the thresholds do not need to be finely judged.
    /// </para>
    /// <para>
    /// This test found a real bug and now guards the fix. Grazing rays used to exhaust the march's step
    /// budget while still converging and return null, so line of sight passed through up to 21 m of
    /// hillside. Before the fix this set reported one deep tunnel and a worst penetration of 2 voxels;
    /// after it, zero and zero. See <c>AGrazingRayReportsTheSurfaceRatherThanRunningOutOfSteps</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoRayPassesThroughSolidTerrain()
    {
        var map = World.Value;
        const float range = 60f;
        const float probeStep = 0.05f;

        // A real tunnel is metres long AND voxel-deep. Both conditions, because each alone has a
        // benign case: a long shallow run is a tangent skim, and a deep short one is the sliver right
        // at an accepted hit.
        // Catastrophic tunnelling only. The stored field saturates at 2.54 voxels, so a ray that has
        // genuinely crossed a hillside reads deep AND stays deep; these thresholds sit an order of
        // magnitude above the near-tangent skimming this terrain actually produces, so the test does
        // not need finely judged numbers to separate the two.
        const float MaximumSolidRunMetres = 1.0f;
        const float MaximumPenetrationVoxels = 2.0f;

        var rays = CombatRays(map, 400);
        var failures = new List<string>();
        int checkedRays = 0;
        int hits = 0;
        float worstRun = 0f;
        float worstDepth = 0f;

        foreach (var (origin, direction) in rays)
        {
            var hit = TerrainRaycast.Cast(map, origin, direction, range);

            // A ray that starts inside terrain reports its own origin; there is nothing before it.
            if (hit is { Distance: <= 0.001f }) continue;

            float end = hit?.Distance ?? range;
            if (hit is not null) hits++;
            checkedRays++;

            float run = 0f;
            float depth = 0f;

            // Stop a touch short: the accepted hit sits within bisection tolerance of the crossing, so
            // the last sliver legitimately reads as solid.
            for (float t = 0f; t < end - 0.05f; t += probeStep)
            {
                if (!TerrainCollision.TrySampleRaw(map, origin + direction * t, out float raw))
                    break;   // left the loaded world; the march would have stopped here too

                if (raw < 0f)
                {
                    run += probeStep;
                    depth = MathF.Max(depth, -raw);
                }
                else
                {
                    // Reset BOTH. Carrying depth across a gap conflates "deep somewhere on this ray"
                    // with "deep in this run", and the whole point is to characterise a single crossing.
                    run = 0f;
                    depth = 0f;
                }

                worstRun = MathF.Max(worstRun, run);
                worstDepth = MathF.Max(worstDepth, depth);

                if (run > MaximumSolidRunMetres && depth > MaximumPenetrationVoxels)
                {
                    failures.Add(
                        $"ray from {origin} dir {direction}: {run:0.00} m of continuous solid "
                        + $"at up to {depth:0.00} voxels deep, before the march reported "
                        + $"{(hit is null ? "a miss" : $"a hit at {end:0.00} m")}");
                    break;
                }
            }
        }

        output.WriteLine(
            $"{checkedRays} rays verified densely, {hits} hits; worst solid run {worstRun:0.00} m, "
            + $"worst penetration {worstDepth:0.00} voxels; {failures.Count} passed through terrain");
        foreach (string failure in failures.Take(5)) output.WriteLine(failure);

        Assert.Empty(failures);
    }

    /// <summary>
    /// A ray that grazes a slope must still report the surface it eventually reaches.
    /// </summary>
    /// <remarks>
    /// Regression for the step-budget exhaustion bug. This ray approaches a slope at a shallow angle,
    /// so each step is proportional to the shrinking distance and the march converges geometrically
    /// rather than crossing. It needed about 153 steps to reach the arrival threshold and the budget
    /// allowed 119, so it stopped at 37.8 m — short of solid ground that starts at 38.75 m — and
    /// returned null, which the caller cannot tell apart from open sky.
    /// <para>
    /// The gameplay symptom was line of sight passing through a rise: an NPC shooting someone up a
    /// gentle hill it should not be able to see.
    /// </para>
    /// </remarks>
    [Fact]
    public void AGrazingRayReportsTheSurfaceRatherThanRunningOutOfSteps()
    {
        var map = World.Value;
        var origin = new Vector3(268f, 23.83f, 170f);
        var direction = Vector3.Normalize(new Vector3(0.9761764f, -0.01382579f, 0.21653756f));

        var hit = TerrainRaycast.Cast(map, origin, direction, 60f);

        Assert.True(hit is not null, "Grazing ray reported a miss through 21 m of solid terrain.");

        // Ground truth: terrain begins at 38.75 m along this ray.
        Assert.InRange(hit!.Value.Distance, 30f, 40f);
    }

    /// <summary>
    /// A reported hit must actually be at the surface, not merely somewhere ahead of it.
    /// </summary>
    /// <remarks>
    /// Guards the other direction. Tunnelling reports a hit too late; a broken arrival test reports one
    /// too early, which would read as terrain blocking line of sight through open air — an NPC
    /// refusing to shoot at something it can plainly see.
    /// </remarks>
    [Fact]
    public void EveryReportedHitSitsOnTheSurface()
    {
        var map = World.Value;
        const float range = 60f;

        var rays = CombatRays(map, 400);
        var failures = new List<string>();
        int hits = 0;

        foreach (var (origin, direction) in rays)
        {
            if (TerrainRaycast.Cast(map, origin, direction, range) is not { } hit) continue;
            if (hit.Distance <= 0.001f) continue;
            hits++;

            // Just before the hit is air, just after is solid. Both must hold for it to be the surface.
            if (TerrainCollision.TrySampleRaw(map, origin + direction * (hit.Distance - 0.15f), out float before)
                && before <= -SurfaceBand)
            {
                failures.Add($"hit at {hit.Distance:0.00} m has solid 0.15 m BEFORE it (raw {before:0.000})");
                continue;
            }

            if (TerrainCollision.TrySampleRaw(map, origin + direction * (hit.Distance + 0.15f), out float after)
                && after > SurfaceBand)
            {
                failures.Add($"hit at {hit.Distance:0.00} m has air 0.15 m AFTER it (raw {after:0.000})");
            }
        }

        output.WriteLine($"{hits} hits checked, {failures.Count} not on a surface");
        foreach (string failure in failures.Take(5)) output.WriteLine(failure);

        Assert.Empty(failures);
    }
}
