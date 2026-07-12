
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

    // Weapon. Null until the server spawns an EquippedWeapon object owned by us —
    // the composition root bridges ObjectRegistry spawns to Equip/Unequip. Ammo and
    // timers are PREDICTED with the same WeaponConfig numbers the server enforces;
    // the replicated object stays the server's truth and re-seeds us on equip.
    public NetObject? Weapon { get; private set; }
    public WeaponStats Stats { get; private set; }
    public bool IsArmed => Weapon != null;

    public int Ammo { get; private set; }
    public bool IsReloading => reloadTicksLeft > 0;
    private int cooldownTicks;
    private int reloadTicksLeft;

    // Sim -> view: raised once per accepted (predicted) shot, the same boundary
    // pattern as the registries' events. Carries the shot's origin and direction.
    public event Action<Vector3, Vector3>? ShotFired;

    public void Equip(NetObject weapon)
    {
        Weapon = weapon;
        Stats = WeaponConfig.Get(weapon.Weapon.Type);
        Ammo = weapon.Weapon.CurrentAmmo;   // seed prediction from replicated truth
        cooldownTicks = 0;
        reloadTicksLeft = 0;
    }

    public void Unequip(NetObject weapon)
    {
        if (!ReferenceEquals(Weapon, weapon)) return;   // despawn of some older weapon
        Weapon = null;
        Ammo = 0;
    }

    public void TryFire(Vector3 direction)
    {
        if (!IsArmed || cooldownTicks > 0 || IsReloading || Ammo == 0) return;

        cooldownTicks = Stats.TicksPerShot;
        Ammo--;

        var origin = Position + new Vector3(0f, GunConfig.MuzzleHeight, 0f);
        network.SendFire(new PlayerFireData
        {
            Sequence = sequence,
            Origin = origin,
            Direction = direction,
        });
        ShotFired?.Invoke(origin, direction);
    }

    public void TryReload()
    {
        if (!IsArmed || IsReloading || Ammo == Stats.MagazineCapacity) return;
        reloadTicksLeft = Stats.ReloadTicks;   // Ammo refills when this reaches 0, in Update
        network.SendReload();
    }

    public LocalPlayer(NetworkManager network) => this.network = network;

    public void Update(Vector3 intent, float dt)
    {
        // Sample input at FixedDt now
        accumulator += dt;

        while (accumulator >= NetworkConfig.FixedDt)
        {
            accumulator -= NetworkConfig.FixedDt;

            // Weapon timers count fixed TICKS, inside this loop on purpose: the
            // server gates by tick, so a frame-counted cooldown would let a 60fps
            // client predict shots the server then silently rejects.
            if (cooldownTicks > 0) cooldownTicks--;
            if (reloadTicksLeft > 0 && --reloadTicksLeft == 0)
                Ammo = Stats.MagazineCapacity;     // reload complete
            var move = new PlayerInputData { Sequence = sequence++, Intent = intent, State = State, Yaw = Yaw };
            Position = PlayerMovement.Step(Position, move.Intent, move.State, NetworkConfig.FixedDt); //predict
            pendingMoves.Enqueue(move);
            network.SendInput(move);
        }
    }

    public void Reconcile(Vector3 serverPosition, uint lastProcessedSequence)
    {
        // Discard all pending moves the server has already simulated 
        while (pendingMoves.Count > 0 && pendingMoves.Peek().Sequence <= lastProcessedSequence)
            pendingMoves.Dequeue();

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