
using System.Numerics;
using Demiurge;
using Demiurge.GameClient;

public abstract class Player
{
    public ushort Id { get; init; }
    public virtual Vector3 Position { get; set; }
    public PlayerStateFlags State { get; set; }
    public float Yaw { get; set; }

    /// <summary>Look angle above the horizon, radians, positive is up. Drives the head and gun aim,
    /// and the direction a shot travels; the BODY still only yaws.</summary>
    public float Pitch { get; set; }
}

// netcode writes, view reads
public class RemotePlayer : Player
{
    public SnapshotBuffer Snapshots { get; } = new();

}

public class LocalPlayer : Player
{
    private readonly NetworkManager network;
    private readonly TerrainState terrain;
    private readonly WeaponMount mount;
    private readonly Queue<PlayerInputData> pendingMoves = new(); // sent but not acked
    private uint sequence;
    private float accumulator;

    /// <summary>
    /// Predicted movement state, stepped by the same <see cref="PlayerMovement.Step"/> the server runs
    /// authoritatively. A field so it can be passed by ref.
    /// </summary>
    public MoveState Move;

    public override Vector3 Position
    {
        get => Move.Position;
        set => Move.Position = value;
    }

    /// <summary>
    /// A correction this large is worth knowing about. Was 1 mm when the step was flat arithmetic;
    /// the collision step is long enough that a genuine disagreement is never this small, and both
    /// ends run identical code over identical voxel bytes so it should stay near zero regardless.
    /// </summary>
    private const float ReconcileWarnDistance = 0.01f;

    public NetObject? Status {get; set;}


    // Weapon. Null until the server spawns a Weapon-masked item object owned by
    // us — the composition root bridges ObjectRegistry spawns to Equip/Unequip. Ammo and
    // timers are PREDICTED with the same ItemConfig numbers the server enforces;
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
        Stats = WeaponConfig.Require(weapon.Item.Type);
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

    /// <summary>
    /// Fires at a point in the world rather than along a direction, and that distinction is the
    /// whole reason shots land where the reticle is.
    ///
    /// The reticle marks where the CAMERA's line of sight lands, but a bullet leaves the MUZZLE,
    /// which is about 0.3 m to one side of the camera and 0.6 m below it. Firing along the camera's
    /// direction sends the bullet on a ray PARALLEL to the camera's — and parallel rays never meet,
    /// so the impact sat permanently down and to the left of the reticle by exactly that offset, at
    /// every range. Aiming AT the point converges the two instead.
    ///
    /// Taking a target also puts the aiming where the origin is known. The caller does not have the
    /// muzzle position — it depends on the weapon and the pitch — so a caller computing a direction
    /// could not have accounted for it.
    /// </summary>
    public void TryFire(Vector3 aimPoint, double renderTick)
    {
        if (!IsArmed || cooldownTicks > 0 || IsReloading || Ammo == 0) return;

        // The barrel of the gun we are actually holding, not a constant height on the
        // player's centre axis. WeaponMount measures it off the same model the renderer
        // draws, in the same pose the renderer is drawing (firing is aiming-only), so the
        // tracer leaves the visible muzzle. Pitch swings the barrel about the chest exactly
        // as the bone override swings it on screen; only the yaw is the body's.
        var origin = Position + Vector3.Transform(
            mount.Muzzle(Weapon!.Item.Type, Pitch), Quaternion.CreateFromYawPitchRoll(Yaw, 0f, 0f));

        // Degenerate only if the aim point is inside the muzzle; spend no ammo on it.
        var toTarget = aimPoint - origin;
        if (toTarget.LengthSquared() < 1e-6f) return;
        var direction = Vector3.Normalize(toTarget);

        cooldownTicks = Stats.TicksPerShot;
        Ammo--;

        network.SendFire(new PlayerFireData
        {
            Sequence = sequence,
            Origin = origin,
            Direction = direction,
            RenderTick = (float)renderTick
        });
        ShotFired?.Invoke(origin, direction);
    }

    public void TryReload()
    {
        if (!IsArmed || IsReloading || Ammo == Stats.MagazineCapacity) return;
        reloadTicksLeft = Stats.ReloadTicks;   // Ammo refills when this reaches 0, in Update
        network.SendReload();
    }

    /// <summary>E pressed: ask the server to pick up / swap whatever is nearby.
    /// Nothing is predicted — the outcome arrives as ordinary object
    /// spawn/despawn replication and flows through Equip/Unequip.</summary>
    public void TryInteract() => network.SendInteract();

    public LocalPlayer(NetworkManager network, TerrainState terrain, WeaponMount mount)
    {
        this.network = network;
        this.terrain = terrain;
        this.mount = mount;
    }

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
            var move = new PlayerInputData { Sequence = sequence++, Intent = intent, State = State, Yaw = Yaw, Pitch = Pitch };
            network.SendInput(move);

            // Prediction needs the same terrain the server is stepping against. Until ours has
            // streamed in, the shared step would read unloaded chunks as impassable and wall us in
            // place while the server walks us normally — so follow authority instead and keep SENDING
            // input, which is what keeps it walking us. Nothing is queued for replay because nothing
            // was predicted.
            if (!terrain.FootprintLoaded(Move.Position))
            {
                pendingMoves.Clear();
                continue;
            }

            PlayerMovement.Step(terrain.Map, ref Move, move.Intent, move.State, NetworkConfig.FixedDt);
            pendingMoves.Enqueue(move);
        }
    }

    public void Reconcile(MoveState authoritative, uint lastProcessedSequence)
    {
        // Discard all pending moves the server has already simulated
        while (pendingMoves.Count > 0 && pendingMoves.Peek().Sequence <= lastProcessedSequence)
            pendingMoves.Dequeue();

        var predicted = Move.Position;

        Move = authoritative;                           // snap to authority...
        foreach (var move in pendingMoves)              // ...then re-apply what it hasn't seen
            PlayerMovement.Step(terrain.Map, ref Move, move.Intent, move.State, NetworkConfig.FixedDt);

        // Diagnostic: in the happy path replay reproduces the prediction exactly.
        // Any hit here means client and server sims disagreed (or a bug).
        float error = Vector3.Distance(predicted, Move.Position);
        if (error > ReconcileWarnDistance)
            Console.WriteLine($"[Reconcile] correction of {error:F4} at seq {lastProcessedSequence}");
    }
}