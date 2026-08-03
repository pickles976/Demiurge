using System.Globalization;

namespace Demiurge
{
    /// <summary>
    /// The two budgets, in milliseconds, that the performance targets are stated against.
    /// </summary>
    /// <remarks>
    /// Derived rather than written down twice. The tick budget follows
    /// <see cref="NetworkConfig.TickRate"/> for the same reason everything else tick-related does — a
    /// second copy of "33.3" would silently stop matching the day the rate changes.
    /// </remarks>
    public static class PerformanceTargets
    {
        /// <summary>60 FPS.</summary>
        public const float ClientFrameBudgetMs = 1000f / 60f;

        /// <summary>One server tick.</summary>
        public const float ServerTickBudgetMs = 1000f / NetworkConfig.TickRate;

        /// <summary>
        /// The fraction of samples that must meet their budget.
        /// </summary>
        /// <remarks>
        /// This is the whole point of measuring percentiles rather than averages. "60 FPS" as a mean is
        /// satisfied by 115 fps for half a second and 20 fps for the other half, which is exactly the
        /// experience the target exists to forbid.
        /// </remarks>
        public const float RequiredFraction = 0.99f;
    }

    /// <summary>
    /// What a window of timings looked like.
    /// </summary>
    /// <remarks>
    /// <paramref name="WithinBudget"/> and <paramref name="P99"/> say the same thing two ways, on
    /// purpose: the fraction answers "are we meeting the requirement", the percentile answers "by how
    /// much are we missing it". A run can pass the first and still be worth looking at through the
    /// second.
    /// </remarks>
    public readonly record struct PercentileSummary(
        int Count,
        float P50,
        float P95,
        float P99,
        float Worst,
        float WithinBudget,
        float BudgetMs)
    {
        /// <summary>Whether this window met the target.</summary>
        public bool MeetsTarget => WithinBudget >= PerformanceTargets.RequiredFraction;

        /// <summary>
        /// False when the window holds too few samples for its p99 to mean anything.
        /// </summary>
        /// <remarks>
        /// With fewer than 100 samples the 99th percentile IS the maximum — one unlucky frame decides
        /// it. Reporting that as a percentile invites reading noise as a trend, so it is flagged rather
        /// than quietly printed.
        /// </remarks>
        public bool P99IsMeaningful => Count >= 100;

        public string Format(string label)
        {
            string verdict = MeetsTarget ? "OK" : "MISS";
            string caveat = P99IsMeaningful ? string.Empty : " (p99 unresolved, <100 samples)";

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{label}: {Count} samples | p50 {P50:F2} ms | p95 {P95:F2} | p99 {P99:F2} | worst {Worst:F2} "
                + $"| within {BudgetMs:F2} ms: {WithinBudget * 100f:F1}% [{verdict}]{caveat}");
        }
    }

    /// <summary>
    /// A rolling window of timings that reports percentiles and how often a budget was met.
    /// </summary>
    /// <remarks>
    /// Exists because the performance targets are stated as "99% of the time", and the per-second
    /// average-plus-worst lines cannot verify that. An average hides the tail entirely, and a single
    /// worst case is one sample — neither distinguishes a steady 60 fps from a bimodal mess with the
    /// same mean.
    /// <para>
    /// The window has to be long enough that its top 1% is more than one sample: at 1024 entries the
    /// p99 is drawn from the worst ten, which is a tail rather than an anecdote. That is also why this
    /// reports on a slower cadence than the existing breakdown lines — a one-second window at 30 TPS
    /// holds 30 samples and cannot resolve a p99 at all.
    /// </para>
    /// <para>
    /// SINGLE WRITER. The client's frame window is written from the main thread and the server's tick
    /// window from the server thread; they are separate instances and never shared. Do not add a second
    /// writer without adding a lock.
    /// </para>
    /// </remarks>
    public sealed class PercentileWindow
    {
        private readonly float[] samples;
        private readonly float[] scratch;
        private readonly float budgetMs;
        private int count;
        private int next;

        public PercentileWindow(float budgetMs, int capacity = 1024)
        {
            if (capacity < 100)
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "A window under 100 samples cannot resolve a 99th percentile — its p99 is just the maximum.");

            this.budgetMs = budgetMs;
            samples = new float[capacity];
            scratch = new float[capacity];
        }

        public int Count => count;

        public int Capacity => samples.Length;

        public void Add(float milliseconds)
        {
            samples[next] = milliseconds;
            next = (next + 1) % samples.Length;
            if (count < samples.Length) count++;
        }

        public void Clear()
        {
            count = 0;
            next = 0;
        }

        public PercentileSummary Summarize()
        {
            if (count == 0) return new PercentileSummary(0, 0f, 0f, 0f, 0f, 1f, budgetMs);

            Array.Copy(samples, scratch, count);
            Array.Sort(scratch, 0, count);

            int within = 0;
            for (int i = 0; i < count; i++)
                if (scratch[i] <= budgetMs) within++;
                else break;   // sorted, so everything past here is over budget

            return new PercentileSummary(
                count,
                Quantile(scratch, count, 0.50f),
                Quantile(scratch, count, 0.95f),
                Quantile(scratch, count, 0.99f),
                scratch[count - 1],
                within / (float)count,
                budgetMs);
        }

        /// <summary>
        /// Nearest-rank percentile: the smallest value at or below which <paramref name="fraction"/> of
        /// the samples fall.
        /// </summary>
        /// <remarks>
        /// Nearest-rank rather than interpolated, because these are frame times and an interpolated p99
        /// reports a duration that never actually occurred. Every value printed here is a real frame.
        /// </remarks>
        private static float Quantile(float[] sorted, int length, float fraction)
        {
            int rank = (int)MathF.Ceiling(fraction * length);
            return sorted[Math.Clamp(rank - 1, 0, length - 1)];
        }
    }
}
