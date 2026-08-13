
using System.Numerics;
using Demiurge;
using Demiurge.GameClient;

public abstract class Player
{
    public ushort Id { get; init; }
    public virtual Vector3 Position { get; set; }
    public PlayerStateFlags State { get; set; }

    /// <summary>
    /// Whether this man's hands are full of something hauled — so nothing else he owns should be
    /// drawn in them. Off the replicated flag for everybody but the local player, who overrides it
    /// with what he is actually holding because his own is predicted.
    /// </summary>
    public virtual bool IsCarrying => State.HasFlag(PlayerStateFlags.Carrying);

    /// <summary>Whether this man is standing at an emplaced weapon and working it. He does not move
    /// while it is true, and his own client draws a different camera for it.</summary>
    public bool IsOperating => State.HasFlag(PlayerStateFlags.Operating);
    public float Yaw { get; set; }
    public HotbarSlot Hotbar { get; set; } = HotbarSlot.Primary;
    public int Team { get; set; } = 1;
    public uint RespawnTick { get; set; }

    /// <summary>Look angle above the horizon, radians, positive is up. Drives the head and gun aim,
    /// and the direction a shot travels; the BODY still only yaws.</summary>
    public float Pitch { get; set; }

    /// <summary>
    /// Standing on something. Replicated for everyone — the local player overrides it from its own
    /// predicted move state, since a prediction that has already landed should not wait a round trip
    /// to say so.
    /// </summary>
    public virtual bool Grounded { get; set; }

    public virtual bool IsDead => RespawnTick != 0;
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

        /// <summary>
        /// Trunks prediction must collide against, the same set the server steps with. Null until the
        /// session hands it over, and null is correct then: before any tree has been replicated there
        /// are none to hit, and predicting against an empty set matches a server that has told us
        /// about none of them yet.
        /// </summary>
        public TreeColliders? Trees { get; set; }
        private readonly Queue<PlayerInputData> pendingMoves = new(); // sent but not acked
    private uint sequence;
    private float accumulator;
    private bool deathState;

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

    public override bool Grounded
    {
        get => Move.Grounded;
        set => Move.Grounded = value;
    }

    public NetObject? Status {get; set;}
    public override bool IsDead => Status is { Health.Current: 0 } || base.IsDead;


    private sealed class PredictedWeapon
    {
        public required NetObject Object { get; init; }
        public required WeaponStats Stats { get; init; }
        public int Ammo;
        public int Reserve;
        public float CooldownTicks;
        public int ReloadTicksLeft;
        public WeaponSpreadState Spread;
    }

    // Every stored weapon keeps its own predicted ammo/timers while the player scrolls away.
    private readonly Dictionary<HotbarSlot, PredictedWeapon> hotbarWeapons = [];

    /// <summary>
    /// Everything the player is carrying in a hotbar slot, weapon or not.
    ///
    /// Separate from <see cref="hotbarWeapons"/> because that map is the PREDICTION — ammo,
    /// cooldowns, spread — and a shovel has none of those to predict. It has no WeaponConfig row, so
    /// it never carried a WeaponState bit, so it was absent from the only per-slot map there was:
    /// which is why its hotbar slot drew as empty however many thumbnails were on disk. Anything
    /// asking "what is in slot 2" wants this one.
    /// </summary>
    private readonly Dictionary<HotbarSlot, NetObject> hotbarItems = [];
    private PredictedWeapon? ActiveWeapon
        => hotbarWeapons.GetValueOrDefault(Hotbar);

    public NetObject? Weapon => ActiveWeapon?.Object;
    public WeaponStats Stats => ActiveWeapon?.Stats ?? default;
    public bool IsArmed => Weapon != null;

    public int Ammo => ActiveWeapon?.Ammo ?? 0;
    public int Reserve => ActiveWeapon?.Reserve ?? 0;
    public bool IsReloading => ActiveWeapon?.ReloadTicksLeft > 0;
    public float CurrentSpreadMoa => IsArmed
        ? ItemCatalog.HasBehavior(Weapon!.Item.Type, ItemBehavior.Grenade)
            ? 0f
            : ActiveWeapon!.Spread.TotalMoa(State, BallisticsConfig.Require(Weapon.Item.Type))
        : 0f;

    // Sim -> view: raised once per accepted (predicted) shot, the same boundary
    // pattern as the registries' events. Carries the shot's origin and direction.
    public event Action<Vector3, Vector3>? ShotFired;

    /// <summary>Which hotbar slot an owned item occupies. Legacy Hand/admin equips are slot 1.</summary>
    private static HotbarSlot SlotOf(NetObject item)
        => HotbarConfig.TryFromStorageSlot(item.Attachment.Slot, out var stored)
            ? stored
            : HotbarSlot.Primary;

    /// <summary>
    /// What this player is hauling in both hands, or null. Mirrors ServerPlayer.IsCarrying, and
    /// exists as its own field rather than a hotbar entry for the same reason the server keeps a
    /// separate slot: hauling something must not evict the rifle. Note that SlotOf would otherwise
    /// file it under Primary and do exactly that, since it defaults anything it does not recognise.
    /// </summary>
    public NetObject? CarriedItem { get; private set; }

    /// <summary>
    /// Whether the hands are full. Blocks firing, reloading, digging and slot changes, client-side,
    /// so prediction refuses the same inputs the server will.
    ///
    /// Answered from the item itself rather than from the replicated flag, because this player's is
    /// predicted: it has to be true the frame the pickup lands, not a round trip later.
    /// </summary>
    public override bool IsCarrying => CarriedItem is not null;

    /// <summary>Records an owned item in its slot. Every hotbar item goes through here; only the
    /// ones that shoot additionally go through <see cref="Equip"/>.</summary>
    public void Carry(NetObject item)
    {
        if (item.Attachment.Slot == EquipSlot.Carried)
        {
            CarriedItem = item;
            return;
        }
        hotbarItems[SlotOf(item)] = item;
    }

    public void Drop(NetObject item)
    {
        if (ReferenceEquals(CarriedItem, item))
        {
            CarriedItem = null;
            return;
        }

        foreach (var pair in hotbarItems)
        {
            if (!ReferenceEquals(pair.Value, item)) continue;
            hotbarItems.Remove(pair.Key);
            break;
        }
        Unequip(item);
    }

    public void Equip(NetObject weapon)
    {
        var slot = SlotOf(weapon);
        hotbarWeapons[slot] = new PredictedWeapon
        {
            Object = weapon,
            Stats = WeaponConfig.Require(weapon.Item.Type),
            Ammo = weapon.Weapon.CurrentAmmo,
            Reserve = weapon.Weapon.ReserveAmmo,
        };
        hotbarItems[slot] = weapon;
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
        => hotbarItems.GetValueOrDefault(slot);

    public int AmmoIn(HotbarSlot slot)
        => hotbarWeapons.GetValueOrDefault(slot)?.Ammo ?? 0;

    /// <summary>
    /// The prediction's half of <see cref="PlayerMovement.Step"/>'s speedScale: what the item in
    /// this slot does to movement speed. Taken per SLOT rather than from whatever is selected now,
    /// because a replay re-runs old moves and each one has to be stepped with the weight it was sent
    /// with — the server does the same, off the same field on the same input.
    ///
    /// An empty slot weighs nothing, and both ends read the same table for everything else, so a
    /// slot the client has not been told about yet is the only way the two can differ — and it
    /// resolves the moment the object arrives.
    /// </summary>
    /// <remarks>
    /// Hauling overrides the slot, because what you are carrying is what you are moving under — the
    /// slung rifle's weight is not paid twice. This one term is player state rather than per-slot,
    /// so a replay spanning a pickup can disagree with the server for the moves either side of it;
    /// picking something up is rare enough, and settles on the next correction.
    /// </remarks>
    private float MoveSpeedScaleIn(HotbarSlot slot)
        => CarriedItem is { } hauled ? ItemConfig.MoveSpeedScale(hauled.Item.Type)
         : ItemIn(slot) is { } held ? ItemConfig.MoveSpeedScale(held.Item.Type)
         : 1f;

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
        bool throwingGrenade = ItemCatalog.HasBehavior(Weapon!.Item.Type, ItemBehavior.Grenade);
        var ballistics = BallisticsConfig.Require(Weapon.Item.Type);
        var direction = throwingGrenade
            ? aimDirection
            : Spread.SampleDirection(
                aimDirection,
                Spread.SigmaRadians(predicted.Spread.TotalMoa(State, ballistics)),
                Spread.ShotSeed(Id, sequence));
        if (direction == Vector3.Zero) return;

        // Add rather than assign so a fractional cadence keeps the sub-tick phase left over from
        // the previous interval. At 1.5 ticks this alternates one- and two-tick gaps: exactly 20 Hz
        // over a 30 Hz simulation instead of rounding to 15 or 30.
        predicted.CooldownTicks += predicted.Stats.TicksPerShot;
        predicted.Ammo--;
        if (throwingGrenade && predicted.Ammo > 0)
            predicted.ReloadTicksLeft = predicted.Stats.ReloadTicks;
        else if (!throwingGrenade)
            predicted.Spread.AddRecoil(ballistics, State);

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
            || ItemCatalog.HasBehavior(Weapon!.Item.Type, ItemBehavior.Grenade)
            || IsReloading
            || Ammo == Stats.MagazineCapacity
            || Reserve <= 0)   // dry: no reload animation for a reload that cannot happen
            return;
        ActiveWeapon!.ReloadTicksLeft = Stats.ReloadTicks;
        network.SendReload();
    }

    /// <summary>E pressed: ask the server to pick up / swap whatever is nearby.
    /// Nothing is predicted — the outcome arrives as ordinary object
    /// spawn/despawn replication and flows through Equip/Unequip.</summary>
    public void TryInteract() => network.SendInteract();

    /// <summary>F: get on or off the emplaced thing in reach, rather than picking it up.</summary>
    public void TryUse() => network.SendUse();

    /// <summary>
    /// The kit to be issued at the next respawn wave.
    ///
    /// Held here as well as on the server because the picker has to show what you chose the instant
    /// you choose it, and the choice is not observable in the world until you are already holding
    /// the gun. It is a request, not a prediction: nothing about the man you are looking at changes,
    /// so there is no state to reconcile if the server disagrees.
    /// </summary>
    public PlayerClass SelectedClass { get; private set; } = PlayerClasses.Default;

    public void SelectClass(PlayerClass playerClass)
    {
        SelectedClass = playerClass;
        network.SendSelectClass(playerClass);
    }

    /// <summary>
    /// Ask for a bomb on a point. Not predicted at all — there is no local effect to show and the
    /// dispersion is the server's to sample, so the request goes and the bomb arrives replicated.
    /// </summary>
    public void TryFireMortar(System.Numerics.Vector3 target)
        => network.SendMortarFire(new MortarFireData { Target = target });

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
                if (predicted.CooldownTicks > 0) predicted.CooldownTicks -= 1f;
                if (predicted.ReloadTicksLeft > 0
                    && --predicted.ReloadTicksLeft == 0
                    && !ItemCatalog.HasBehavior(predicted.Object.Item.Type, ItemBehavior.Grenade))
                {
                    // The same arithmetic the server runs in WeaponSystem.ApplyReload: top the
                    // magazine up from the pouches rather than filling it, or the HUD would promise
                    // rounds the server is about to say do not exist.
                    int loaded = Math.Min(
                        predicted.Stats.MagazineCapacity - predicted.Ammo,
                        predicted.Reserve);
                    predicted.Ammo += loaded;
                    predicted.Reserve -= loaded;
                }
            }
            if (IsArmed && !ItemCatalog.HasBehavior(Weapon!.Item.Type, ItemBehavior.Grenade))
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

            PlayerMovement.Step(
                terrain.Map, ref Move, move.Intent, move.State, NetworkConfig.FixedDt,
                MoveSpeedScaleIn(move.Hotbar), Trees);
            pendingMoves.Enqueue(move);
        }
    }

    /// <summary>
    /// Stops local prediction while authority holds the actor at its corpse. Clearing unacknowledged
    /// moves is essential: replaying them on every dead-position snapshot would make the killcam
    /// anchor and the cosmetic body drift away from the server's death location.
    /// </summary>
    public void EnterDeath()
    {
        if (deathState) return;
        deathState = true;
        pendingMoves.Clear();
        accumulator = 0f;
        Move.Velocity = Vector3.Zero;
        State = 0;
    }

    public void LeaveDeath()
    {
        if (!deathState) return;
        deathState = false;
        foreach (var predicted in hotbarWeapons.Values)
        {
            predicted.Ammo = predicted.Stats.MagazineCapacity;
            // The magazine is assumed full because the respawn refill may not have landed yet; the
            // reserve has no equally obvious answer (a grenade stack carries none), so it is taken
            // from the replicated state rather than guessed.
            predicted.Reserve = predicted.Object.Weapon.ReserveAmmo;
            predicted.CooldownTicks = 0;
            predicted.ReloadTicksLeft = 0;
            predicted.Spread = default;
        }
    }

    public void Reconcile(MoveState authoritative, uint lastProcessedSequence)
    {
        // Discard all pending moves the server has already simulated
        while (pendingMoves.Count > 0 && pendingMoves.Peek().Sequence <= lastProcessedSequence)
            pendingMoves.Dequeue();

        if (deathState)
        {
            pendingMoves.Clear();
            Move = authoritative;
            return;
        }

        var predicted = Move.Position;

        Move = authoritative;                           // snap to authority...
        foreach (var move in pendingMoves)              // ...then re-apply what it hasn't seen
            PlayerMovement.Step(
                terrain.Map, ref Move, move.Intent, move.State, NetworkConfig.FixedDt,
                MoveSpeedScaleIn(move.Hotbar), Trees);

        // Diagnostic: in the happy path replay reproduces the prediction exactly.
        // Any hit here means client and server sims disagreed (or a bug).
        float error = Vector3.Distance(predicted, Move.Position);
        if (error > ReconcileWarnDistance)
            Console.WriteLine($"[Reconcile] correction of {error:F4} at seq {lastProcessedSequence}");
    }
}
