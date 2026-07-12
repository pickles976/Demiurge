using System.Numerics;
using Demiurge;

public class SnapshotBuffer
{
    private readonly Queue<(uint Tick, Vector3 Position)> snapshots = new();
    private long newestArrival;

    public uint NewestTick { get; private set; }
    public double SecondsSinceNewest => (System.Diagnostics.Stopwatch.GetTimestamp() - newestArrival) / (double)System.Diagnostics.Stopwatch.Frequency;

    public void Store(uint tick, Vector3 position)
    {
        // Unreliable channel: drop reordered/duplicate packets so tick math never underflows
        if (snapshots.Count > 0 && tick <= NewestTick) return;

        snapshots.Enqueue((tick, position));
        NewestTick = tick;
        newestArrival = System.Diagnostics.Stopwatch.GetTimestamp();

        while (tick - snapshots.Peek().Tick > NetworkConfig.TickRate) // keep 1s of history
            snapshots.Dequeue();
    }

    public Vector3 GetInterpolated(double renderTick, Vector3 fallback)
    {
        if (snapshots.Count == 0) return fallback;

        (uint Tick, Vector3 Position)? previous = null;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Tick >= renderTick)
            {
                if (previous is not { } older) return snapshot.Position;
                double t = (renderTick - older.Tick) / (snapshot.Tick - older.Tick);
                return Vector3.Lerp(older.Position, snapshot.Position, (float)t);
            }
            previous = snapshot;
        }
        return snapshots.Last().Position;
    }
}