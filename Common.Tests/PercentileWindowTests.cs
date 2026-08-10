using Xunit;

namespace Demiurge.Tests;

/// <summary>
/// Pure percentile arithmetic. Worth testing precisely because it is what the performance targets
/// will be judged by — a percentile that is quietly wrong turns "we meet 30 TPS 99% of the time" into
/// a claim nobody can check.
/// </summary>
public class PercentileWindowTests
{
    private static PercentileWindow Filled(float budgetMs, int capacity, IEnumerable<float> values)
    {
        var window = new PercentileWindow(budgetMs, capacity);
        foreach (float value in values) window.Add(value);
        return window;
    }

    [Fact]
    public void PercentilesOfAKnownDistribution()
    {
        // 1..100 ms. Nearest-rank: p50 is the 50th value, p95 the 95th, p99 the 99th.
        var window = Filled(1000f, 128, Enumerable.Range(1, 100).Select(v => (float)v));
        var summary = window.Summarize();

        Assert.Equal(100, summary.Count);
        Assert.Equal(50f, summary.P50);
        Assert.Equal(95f, summary.P95);
        Assert.Equal(99f, summary.P99);
        Assert.Equal(100f, summary.Worst);
    }

    [Fact]
    public void EveryReportedValueIsARealSample()
    {
        // Nearest-rank never interpolates, so a bimodal set reports one of its two modes and never an
        // average of them. A frame time that never occurred would be a misleading thing to print.
        var values = Enumerable.Repeat(5f, 90).Concat(Enumerable.Repeat(200f, 10));
        var summary = Filled(1000f, 128, values).Summarize();

        Assert.Equal(5f, summary.P50);
        Assert.Equal(200f, summary.P95);
        Assert.Equal(200f, summary.P99);
    }

    [Fact]
    public void WithinBudgetCountsSamplesThatMetIt()
    {
        // 97 good frames and 3 bad ones is 97% — a MISS against a 99% requirement, even though the
        // mean would look entirely healthy.
        var values = Enumerable.Repeat(10f, 97).Concat(Enumerable.Repeat(50f, 3));
        var summary = Filled(16.67f, 128, values).Summarize();

        Assert.Equal(0.97f, summary.WithinBudget, 3);
        Assert.False(summary.MeetsTarget);
    }

    [Fact]
    public void MeetsTargetAtExactlyNinetyNinePercent()
    {
        var values = Enumerable.Repeat(10f, 99).Append(50f);
        var summary = Filled(16.67f, 128, values).Summarize();

        Assert.Equal(0.99f, summary.WithinBudget, 3);
        Assert.True(summary.MeetsTarget);
    }

    [Fact]
    public void TheMeanCanPassWhileTheTargetFails()
    {
        // The exact failure mode percentiles exist to catch: half the window at 115 fps and half at
        // 20 fps averages to a healthy-looking number and feels terrible.
        var values = Enumerable.Repeat(8.7f, 50).Concat(Enumerable.Repeat(50f, 50)).ToList();

        float mean = values.Average();
        Assert.True(mean < PerformanceTargets.ClientFrameBudgetMs * 2f);

        var summary = Filled(PerformanceTargets.ClientFrameBudgetMs, 128, values).Summarize();
        Assert.Equal(0.5f, summary.WithinBudget, 3);
        Assert.False(summary.MeetsTarget);
    }

    [Fact]
    public void OldSamplesFallOutOfTheWindow()
    {
        // Capacity 100, 100 bad samples then 100 good ones: the bad ones must be entirely gone.
        var window = new PercentileWindow(16.67f, 100);
        for (int i = 0; i < 100; i++) window.Add(500f);
        for (int i = 0; i < 100; i++) window.Add(1f);

        var summary = window.Summarize();
        Assert.Equal(100, summary.Count);
        Assert.Equal(1f, summary.Worst);
        Assert.Equal(1f, summary.WithinBudget);
    }

    [Fact]
    public void P99IsFlaggedAsUnresolvedBelowAHundredSamples()
    {
        var summary = Filled(16.67f, 128, Enumerable.Repeat(5f, 99)).Summarize();

        Assert.False(summary.P99IsMeaningful);
        Assert.True(Filled(16.67f, 128, Enumerable.Repeat(5f, 100)).Summarize().P99IsMeaningful);
    }

    [Fact]
    public void AWindowTooSmallForAP99IsRejected()
    {
        // Constructing one would produce a p99 that is just the maximum, dressed up as a percentile.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PercentileWindow(16.67f, 99));
    }

    [Fact]
    public void AnEmptyWindowDoesNotReportAFailure()
    {
        var summary = new PercentileWindow(16.67f).Summarize();

        Assert.Equal(0, summary.Count);
        Assert.True(summary.MeetsTarget);
    }

    [Fact]
    public void BudgetsFollowTheTickRate()
    {
        // The server budget must track NetworkConfig, not be a second copy of 33.3 that drifts.
        Assert.Equal(1000f / NetworkConfig.TickRate, PerformanceTargets.ServerTickBudgetMs, 4);
    }
}
