
using System.Numerics;
using Demiurge;
using Demiurge.GameClient;

public abstract class Player
{
    public ushort Id { get; init; }
    public virtual Vector3 Position { get; set; }
    public PlayerStateFlags State { get; set; }
    public float Yaw { get; set; }
    public HotbarSlot Hotbar { get; set; } = HotbarSlot.Primary;
    public int Team { get; set; } = 1;

    /// <summary>Look angle above the horizon, radians, positive is up. Drives the head and gun aim,
    /// and the direction a shot travels; the BODY still only yaws.</summary>
    public float Pitch { get; set; }
}

// netcode writes, view reads
public class RemotePlayer : Player
{
    public SnapshotBuffer Snapshots { get; } = new();
    public Vector3 Velocity { get; set; }
}

    public class LocalPlayer : Player
    {
        private readonly NetworkManager network;
        private readonly TerrainState terrain;
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


    private sealed class PredictedWeapon
    {
        public required NetObject Object { get; init; }
        public required WeaponStats Stats { get; init; }
        public int Ammo;
        public int CooldownTicks;
        public int ReloadTicksLeft;
        public WeaponSpreadState Spread;
    }

    // Every stored weapon keeps its own predicted ammo/timers while the player scrolls away.
    private readonly Dictionary<HotbarSlot, PredictedWeapon> hotbarWeapons = [];
    private PredictedWeapon? ActiveWeapon
        => hotbarWeapons.GetValueOrDefault(Hotbar);

    public NetObject? Weapon => ActiveWeapon?.Object;
    public WeaponStats Stats => ActiveWeapon?.Stats ?? default;
    public bool IsArmed => Weapon != null;

    public int Ammo => ActiveWeapon?.Ammo ?? 0;
    public bool IsReloading => ActiveWeapon?.ReloadTicksLeft > 0;
    public float CurrentSpreadMoa => IsArmed
        ? Weapon!.Item.Type == ItemType.Grenade
            ? 0f
            : ActiveWeapon!.Spread.TotalMoa(State, BallisticsConfig.Require(Weapon.Item.Type))
        : 0f;

    // Sim -> view: raised once per accepted (predicted) shot, the same boundary
    // pattern as the registries' events. Carries the shot's origin and direction.
    public event Action<Vector3, Vector3>? ShotFired;

    public void Equip(NetObject weapon)
    {
        HotbarSlot slot = HotbarConfig.TryFromStorageSlot(weapon.Attachment.Slot, out var stored)
            ? stored
            : HotbarSlot.Primary; // legacy Hand/admin equips are slot 1
        hotbarWeapons[slot] = new PredictedWeapon
        {
            Object = weapon,
            Stats = WeaponConfig.Require(weapon.Item.Type),
            Ammo = weapon.Weapon.CurrentAmmo,
        };
    }

    public void Unequip(NetObject weapon)
    {
        HotbarSlot? removed = null;
        foreach (var pair in hotbarWeapons)
        {
            if (!ReferenceEquals(pair.Value.Object, weapon)) continue;
            removed = pair.Key;
            break;
        }
        if (removed is { } slot) hotbarWeapons.Remove(slot);
    }

    public void SelectHotbar(HotbarSlot slot)
    {
        if (HotbarConfig.IsValid(slot)) Hotbar = slot;
    }

    public NetObject? ItemIn(HotbarSlot slot)
        => hotbarWeapons.GetValueOrDefault(slot)?.Object;

    public int AmmoIn(HotbarSlot slot)
        => hotbarWeapons.GetValueOrDefault(slot)?.Ammo ?? 0;

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
        /// The caller supplies the muzzle origin from the current view-model, so the predicted shot,
        /// tracer, and server request all start from the same barrel the player sees.
    /// </summary>
    public void TryFire(Vector3 aimPoint, double renderTick, Vector3 origin)
    {
        var predicted = ActiveWeapon;
        if (predicted == null
            || predicted.CooldownTicks > 0
            || predicted.ReloadTicksLeft > 0
            || predicted.Ammo == 0)
            return;

        // Degenerate only if the aim point is inside the muzzle; spend no ammo on it.
        var toTarget = aimPoint - origin;
        if (toTarget.LengthSquared() < 1e-6f) return;
        var aimDirection = Vector3.Normalize(toTarget);
        bool throwingGrenade = Weapon!.Item.Type == ItemType.Grenade;
        var ballistics = BallisticsConfig.Require(Weapon.Item.Type);
        var direction = throwingGrenade
            ? aimDirection
            : Spread.SampleDirection(
                aimDirection,
                Spread.SigmaRadians(predicted.Spread.TotalMoa(State, ballistics)),
                Spread.ShotSeed(Id, sequence));
        if (direction == Vector3.Zero) return;

        predicted.CooldownTicks = predicted.Stats.TicksPerShot;
        predicted.Ammo--;
        if (throwingGrenade && predicted.Ammo > 0)
            predicted.ReloadTicksLeft = predicted.Stats.ReloadTicks;
        else if (!throwingGrenade)
            predicted.Spread.AddRecoil(ballistics);

        network.SendFire(new PlayerFireData
        {
            Sequence = sequence,
            Origin = origin,
            // Guns carry the centre of the cone for authoritative server sampling. Grenades have
            // no spread, so their already-lobbed launch direction goes through directly.
            Direction = throwingGrenade ? direction : aimDirection,
            RenderTick = (float)renderTick,
            Hotbar = Hotbar,
        });
        ShotFired?.Invoke(origin, direction);
    }

    public void TryReload()
    {
        if (!IsArmed
            || Weapon!.Item.Type == ItemType.Grenade
            || IsReloading
            || Ammo == Stats.MagazineCapacity)
            return;
        ActiveWeapon!.ReloadTicksLeft = Stats.ReloadTicks;
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
            foreach (var predicted in hotbarWeapons.Values)
            {
                if (predicted.CooldownTicks > 0) predicted.CooldownTicks--;
                if (predicted.ReloadTicksLeft > 0
                    && --predicted.ReloadTicksLeft == 0
                    && predicted.Object.Item.Type != ItemType.Grenade)
                    predicted.Ammo = predicted.Stats.MagazineCapacity;
            }
            if (IsArmed && Weapon!.Item.Type != ItemType.Grenade)
                ActiveWeapon!.Spread.Advance(
                    State,
                    BallisticsConfig.Require(Weapon!.Item.Type),
                    NetworkConfig.FixedDt);
            var move = new PlayerInputData
            {
                Sequence = sequence++,
                Intent = intent,
                State = State,
                Yaw = Yaw,
                Pitch = Pitch,
                Hotbar = Hotbar,
            };
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
