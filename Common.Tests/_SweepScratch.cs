using System.Diagnostics;
using System.Numerics;
using Demiurge;
using Xunit;
using Xunit.Abstractions;

namespace Demiurge.Tests;

public class BudgetSweepScratch(ITestOutputHelper output)
{
    // TerrainLod.PixelErrorBudget is const, so re-derive the equivalent by scaling the viewport
    // height: error scales linearly with it, so height*k is the same as budget/k.
    [Fact]
    public void Sweep()
    {
        float hip = MathF.Tan(74f * MathF.PI / 360f);
        float ads3x = MathF.Tan(56f * MathF.PI / 360f) / 3f;
        var eye = new Vector3(8f, 40f, 8f);

        output.WriteLine("budget | hip LOD0 radius | hip sections (L0) | 3x sections (L0) | hip ms");
        foreach (float budget in new[] { 12.8f, 9.0f, 6.4f, 4.8f, 3.2f, 2.0f })
        {
            float k = TerrainLod.PixelErrorBudget / budget;
            float height = 1080f * k;

            var results = new (int Total, int L0)[2];
            var lenses = new[] { hip, ads3x };
            double ms = 0;

            for (int i = 0; i < 2; i++)
            {
                var lod = new TerrainLod();
                var desired = new HashSet<LodSection>();
                var view = new TerrainView(eye, Vector3.UnitZ, Vector3.UnitY, lenses[i], 16f / 9f,
                                           height, TerrainLod.FrustumMarginDegrees);
                for (int w = 0; w < 10; w++) lod.CollectDesired(view, desired);
                var sw = Stopwatch.StartNew();
                for (int r = 0; r < 50; r++) lod.CollectDesired(view, desired);
                sw.Stop();
                if (i == 0) ms = sw.Elapsed.TotalMilliseconds / 50;

                int l0 = 0;
                foreach (var b in desired) if (b.Level == 0) l0++;
                results[i] = (desired.Count, l0);
                if (lod.LastHitCeiling) output.WriteLine($"   !! budget {budget} lens {i} HIT CEILING");
            }

            output.WriteLine(
                $"{budget,5:F1} | {1433f / budget,10:F0} m | {results[0].Total,6} ({results[0].L0,5}) "
              + $"| {results[1].Total,6} ({results[1].L0,5}) | {ms,5:F2}");
        }
    }
}
