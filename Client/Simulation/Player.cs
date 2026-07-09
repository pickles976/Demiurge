
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
    private readonly Queue<PlayerInputData> pendingMoves = new(); // sent but not acked
    private uint sequence;
    private float accumulator;

    public LocalPlayer(NetworkManager network) => this.network = network;

    public void Update(Vector3 intent, float dt)
    {
        // Sample input at FixedDt now
        accumulator += dt;
        while (accumulator >= NetworkConfig.FixedDt)
        {
            accumulator -= NetworkConfig.FixedDt;
            var move = new PlayerInputData {Sequence = sequence++, Intent = intent, State = State, Yaw = Yaw};
            Position = PlayerMovement.Step(Position, move.Intent, move.State, NetworkConfig.FixedDt); //predict
            pendingMoves.Enqueue(move);
            network.SendInput(move);
        }
    }

    public void Reconcile(Vector3 serverPosition, uint lastProcessedSequence)
    {
        while (pendingMoves.Count > 0 && pendingMoves.Peek().Sequence <= lastProcessedSequence)
              pendingMoves.Dequeue();                     // server already simulated these

        var predicted = Position;

        Position = serverPosition;                      // snap to authority...
        foreach (var move in pendingMoves)              // ...then re-apply what it hasn't seen
            Position = PlayerMovement.Step(Position, move.Intent, move.State, NetworkConfig.FixedDt);

        // Diagnostic: in the happy path replay reproduces the prediction exactly.
        // Any hit here means client and server sims disagreed (or a bug).
        float error = Vector3.Distance(predicted, Position);
        if (error > 0.001f)
            Console.WriteLine($"[Reconcile] correction of {error:F4} at seq {lastProcessedSequence}");
    }
}