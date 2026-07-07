
using System.Numerics;
using Demiurge;
using Demiurge.GameClient;

public abstract class Player
{
    public ushort Id { get; init; }
    public Vector3 Position { get; set; }
    public PlayerStateFlags State { get; set; }
    public float Yaw { get; set; }
}

// netcode writes, view reads
public class RemotePlayer : Player
{
    private readonly Queue<(uint Tick, Vector3 Position)> snapshots = new();

    public uint NewestTick { get; private set; }
    public double SecondsSinceNewestSnapshot => (System.Diagnostics.Stopwatch.GetTimestamp() - newestArrival) / (double)System.Diagnostics.Stopwatch.Frequency;
    private long newestArrival;

    public void StoreSnapshot(uint tick, Vector3 position)
    {
        // Unreliable channel: drop reordered/duplicate packets so tick math never underflows
        if (snapshots.Count > 0 && tick <= NewestTick) return;

        snapshots.Enqueue((tick, position));
        NewestTick = tick;
        newestArrival = System.Diagnostics.Stopwatch.GetTimestamp();

        while (tick - snapshots.Peek().Tick > NetworkConfig.TickRate) // Keep 1s of history
            snapshots.Dequeue();
    }

    public Vector3 GetInterpolatedPosition(double renderTick)
    {
        if (snapshots.Count == 0) return Position;

        (uint Tick, Vector3 Position)? previous = null;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Tick >= renderTick)
            {
                if (previous is not { } older) return snapshot.Position; // renderTick before oldest
                double t = (renderTick - older.Tick) / (snapshot.Tick - older.Tick); // percent between the two snapshots
                return Vector3.Lerp(older.Position, snapshot.Position, (float)t);
            }
            previous = snapshot;
        }
        return snapshots.Last().Position;
    }

}

public class LocalPlayer : Player
{
    private readonly NetworkManager network;
    private uint sequence;

    public LocalPlayer(NetworkManager network) => this.network = network;

    public void Update(Vector3 intent, float dt)
    {
        // Predict locally with the same function the server runs authoritatively.
        Position = PlayerMovement.Step(Position, intent, State, dt);
        network.SendInput(new PlayerInputData { Sequence = sequence++, Intent = intent, State = State, Yaw = Yaw });
    }
}