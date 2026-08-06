using Demiurge;
using Demiurge.GameClient;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

// Hover-and-spin presenter for weapon pickups. Client-side animation over the
// replicated base position — this script OWNS the entity transform, so the
// factory must not also attach a NetTransformScript (they would fight).
public class PickupBobScript : SyncScript
{
    public required NetObject Object { get; init; }

    private const float FloatHeight = 0.4f;    // resting height above the base position
    private const float BobHeight = 0.15f;
    private const float BobHz = 0.5f;
    private const float SpinDegPerSec = 90f;

    private float age;

    public override void Update()
    {
        age += (float)Game.UpdateTime.Elapsed.TotalSeconds;

        float bob = BobHeight * MathF.Sin(2f * MathF.PI * BobHz * age);
        Entity.Transform.Position = Object.Transform.Position.ToStride()
            + Vector3.UnitY * (FloatHeight + bob);
        Entity.Transform.Rotation = Quaternion.RotationY(MathUtil.DegreesToRadians(SpinDegPerSec) * age);
    }
}

// Attaches a worn item's view to its owner's body — driven by the replicated
// Owner and Attachment components. The wire says WHOSE body and WHICH slot;
// the slot keys the client's socket table (ItemCosmetics): a bone name links
// via ModelNodeLinkComponent, null follows the Player_{id} entity root every
// frame. The owner's view may not exist yet when this spawns (reliable
// messages aren't ordered relative to each other), so it retries every frame
// until the player appears.
//
// A hotbar item whose slot is not selected is not hidden: it is WORN, at the
// stowed socket its slot names (rifle on the back, shovel on the hip). That is
// a client-side choice only — the item never leaves its hotbar slot on the wire
// — which is why the stowed sockets sit in ItemCosmetics beside the held ones
// and cost no EquipSlot and no protocol change.
//
// The entity stays at the scene root on purpose: ModelNodeLinkComponent drives
// its world transform from the bone regardless of hierarchy, root-following
// composes the owner's transform explicitly, and root-level entities keep
// ObjectViewFactory.DestroyView's find-by-name working.
public class ItemAttachScript : SyncScript
{
    private readonly record struct RecoilKick(float Back, float Lift, float PitchRadians);

    public required NetObject Object { get; init; }
    public required WeaponMount Mount { get; init; }
    public required PlayerRegistry Registry { get; init; }
    public required Entity CameraEntity { get; init; }
    public required LocalWeaponView WeaponView { get; init; }
    public required ModelLocators Locators { get; init; }

    private const float ViewModelSharpness = 18f;
    private const float RecoilReturnSharpness = 12f;
    private const float MaxRecoilBack = 0.16f;
    private const float MaxRecoilLift = 0.045f;
    private static readonly float MaxRecoilPitch = MathUtil.DegreesToRadians(10f);

    /// <summary>How far the tool travels along the swing, on top of the rotation — a chop that
    /// only pivots reads as a wrist flick.</summary>
    private static readonly Vector3 SwingReach = new(0f, -0.10f, -0.16f);

    // Sprint sway. Slower than the blend so the pose settles before the swing does, and split
    // across two rates because one sine on every axis at once is a metronome rather than a run:
    // the side-to-side is the stride and the rise-and-fall is the footfalls inside it, at double
    // the rate. Everything scales by sprintBlend, which is what stops the weapon snapping into
    // and out of the pose the frame Shift is pressed.
    private const float SprintBlendSharpness = 9f;
    private const float SwayHz = 1.25f;
    private const float SwaySide = 0.035f;
    private const float SwayRise = 0.018f;
    private static readonly float SwayRoll = MathUtil.DegreesToRadians(5f);

    private Entity? owner;
    private string? linkedNode;
    private Vector3 viewGripOffset = WeaponMount.HipGripOffset;
    private bool firstViewFrame = true;
    private int? observedAmmo;
    private float recoilBack;
    private float recoilLift;
    private float recoilPitch;
    private MovingPart? bolt;
    private SoundManager sound = null!;

    /// <summary>Seconds until this shot's bolt is worked, or infinity when none is pending. The
    /// delay is the bolt cycle's own, so the noise arrives with the movement rather than on top of
    /// the shot that caused it.</summary>
    private float boltSoundIn = float.PositiveInfinity;

    private int shotsThisFrame;
    private float sprintBlend;
    private float swayPhase;
    private readonly SwingAnimation swing = new();

    public override void Start()
    {
        if (Object.Has.HasFlag(NetComponents.Weapon))
            observedAmmo = Object.Weapon.CurrentAmmo;
        sound = Services.GetSafeServiceAs<SoundManager>();
        bolt = MovingPart.For(
            Locators, ItemCosmetics.Model(Object.Item.Type), "bolt", WeaponFx.Get(Object.Item.Type).Cycle);
    }

    public override void Update()
    {
        owner ??= Entity.Scene?.Entities.FirstOrDefault(e => e.Name == $"Player_{Object.Owner.PlayerId}");
        if (owner == null) return;
        if (Entity.Get<ModelComponent>() is not { } model) return;

        float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
        if (!Registry.TryGet(Object.Owner.PlayerId, out var player) || player.IsDead)
        {
            model.Enabled = false;
            if (WeaponView.NetworkId == Object.NetworkId) WeaponView.Clear();
            return;
        }

        // A swing is driven by the Shooting flag, which is replicated, so everybody sees the same
        // dig from their own angle. Cycling the bolt is driven by ammo falling, which is likewise
        // replicated — predicted locally, off the wire for everyone else.
        bool holdingTool = Object.Item.Type == ItemType.Shovel;
        swing.Update(holdingTool && IsSelected(player) && player.State.HasFlag(PlayerStateFlags.Shooting), dt);

        shotsThisFrame = ShotsSince(player);

        // The bolt's noise is scheduled off the same BoltCycle that paces its movement, so the two
        // cannot drift apart. A self-loading action has no sound path and never schedules anything.
        var cycle = WeaponFx.Get(Object.Item.Type).Cycle;
        if (shotsThisFrame > 0 && cycle.SoundPath is not null)
            boltSoundIn = cycle.DelaySeconds;
        else
            boltSoundIn -= dt;
        if (boltSoundIn <= 0f && cycle.SoundPath is { } boltSound)
        {
            boltSoundIn = float.PositiveInfinity;
            PlayBoltCycle(player, boltSound);
        }

        if (bolt is { } part)
        {
            // The local player's reload is predicted and therefore a frame ahead of the replicated
            // flag; everyone else's arrives on the movement stream.
            part.SetHeld(IsSelected(player) && (player is LocalPlayer local
                ? local.IsReloading
                : player.State.HasFlag(PlayerStateFlags.Reloading)));
            if (shotsThisFrame > 0) part.Cycle();
            part.Update(model, dt);
        }

        if (!IsSelected(player))
        {
            if (WeaponView.NetworkId == Object.NetworkId) WeaponView.Clear();
            UpdateStowed(model);
            return;
        }

        if (IsLocalViewModel())
        {
            UpdateFirstPersonItem(model, dt);
            return;
        }

        model.Enabled = true;
        Entity.Transform.Scale = new Vector3(ItemCosmetics.WorldScale(Object.Item.Type));
        Seat(ItemCosmetics.GetSocket(Object.Attachment.Slot, Object.Item.Type, Mount)
                 with { Rotation = WeaponMount.HandRotationFor(Object.Item.Type, swing.Angle).ToStride() });
    }

    /// <summary>
    /// The bolt as the listener hears it. Your own rifle is in your hands rather than somewhere in
    /// the world, so it plays flat — the same split, for the same reason, that a reload makes.
    /// </summary>
    private void PlayBoltCycle(Player player, string path)
    {
        if (player is LocalPlayer)
            sound.PlayOneShot(path);
        else
            sound.PlayOneShotSpatial(
                path, Digging.Eye(player.Position).ToStride(), falloff: SoundFalloff.Reload);
    }

    /// <summary>Worn rather than hidden, when the slot has somewhere to wear it.</summary>
    private void UpdateStowed(ModelComponent model)
    {
        // Not for the local player: first person does not render its own body, so there is no back
        // to hang a rifle on — and the bone the socket names belongs to a disabled ModelComponent.
        if (IsLocalViewModel()
            || !HotbarConfig.TryFromStorageSlot(Object.Attachment.Slot, out var hotbar)
            || ItemCosmetics.StowedSocket(hotbar, Object.Item.Type) is not { } socket)
        {
            model.Enabled = false;
            return;
        }

        model.Enabled = true;
        Entity.Transform.Scale = new Vector3(ItemCosmetics.WorldScale(Object.Item.Type));
        Seat(socket);
    }

    /// <summary>
    /// Places the entity at a socket, relinking if the bone changed.
    ///
    /// The link used to latch once, on the reasoning that an item's owner and slot never change in
    /// place. That still holds — but the SOCKET now does, every time the player scrolls the hotbar
    /// and a rifle moves from the hand to the back, so the bone the link points at is re-checked
    /// instead. Seat and rotation are written every frame because a swing changes them.
    /// </summary>
    private void Seat(ItemCosmetics.Socket socket)
    {
        if (socket.Node is not { } node)
        {
            if (linkedNode != null)
            {
                Entity.Remove<ModelNodeLinkComponent>();
                linkedNode = null;
            }
            // Root-worn items follow the owner's root transform every frame —
            // PlayerViewScript drives that entity, this composes the seat on top.
            Entity.Transform.Position = owner!.Transform.Position + Vector3.Transform(socket.Seat, owner.Transform.Rotation);
            Entity.Transform.Rotation = owner.Transform.Rotation * socket.Rotation;
            return;
        }

        if (linkedNode != node)
        {
            if (owner!.Get<ModelComponent>() is not { } ownerModel) return;
            if (linkedNode != null) Entity.Remove<ModelNodeLinkComponent>();
            Entity.Add(new ModelNodeLinkComponent { Target = ownerModel, NodeName = node });
            linkedNode = node;
        }

        Entity.Transform.Position = socket.Seat;
        Entity.Transform.Rotation = socket.Rotation;
    }

    /// <summary>
    /// True for anything the local player is holding, weapon or not. The shovel has no WeaponState
    /// — it is a tool, not a gun — but it still has to be drawn in front of the camera rather than
    /// on a body the first-person view does not render.
    /// </summary>
    private bool IsLocalViewModel()
        => Registry.LocalPlayer is { } local
           && Object.Owner.PlayerId == local.Id
           && (Object.Attachment.Slot == EquipSlot.Hand
               || HotbarConfig.TryFromStorageSlot(Object.Attachment.Slot, out _));

    private bool IsSelected(Player player)
        => !HotbarConfig.TryFromStorageSlot(Object.Attachment.Slot, out var slot)
           || player.Hotbar == slot;

    /// <summary>
    /// How many rounds this item has fired since the last frame — the one signal both the bolt and
    /// the recoil kick run off, computed once per frame so the two cannot disagree.
    ///
    /// Ammo falling IS the shot: predicted locally so the local player's bolt cycles on the frame
    /// they click, and read off the replicated WeaponState for everybody else so a remote rifle
    /// cycles too. A reload raises ammo instead of lowering it and is therefore ignored for free.
    ///
    /// The local reading is taken per SLOT, not from whatever is selected, so switching hotbar slots
    /// does not swap the source between predicted and replicated ammo mid-count — that difference is
    /// a shot or two, and would have cycled the bolt every time you scrolled back to your rifle.
    /// </summary>
    private int ShotsSince(Player player)
    {
        if (!Object.Has.HasFlag(NetComponents.Weapon)) return 0;

        var slot = HotbarConfig.TryFromStorageSlot(Object.Attachment.Slot, out var hotbar)
            ? hotbar
            : HotbarSlot.Primary;   // legacy Hand/admin equips are tracked as slot 1
        int current = player is LocalPlayer local && local.ItemIn(slot)?.NetworkId == Object.NetworkId
            ? local.AmmoIn(slot)
            : Object.Weapon.CurrentAmmo;

        int shots = Math.Max(0, (observedAmmo ?? current) - current);
        observedAmmo = current;
        return shots;
    }

    private void UpdateFirstPersonItem(ModelComponent model, float dt)
    {
        if (CameraEntity.Get<DebugFlyCameraScript>()?.Active == true)
        {
            model.Enabled = false;
            WeaponView.Clear();
            return;
        }
        model.Enabled = true;

        if (linkedNode != null)
        {
            Entity.Remove<ModelNodeLinkComponent>();
            linkedNode = null;
        }

        var local = Registry.LocalPlayer!;
        if (Object.Item.Type == ItemType.Grenade && (local.Ammo == 0 || local.IsReloading))
        {
            model.Enabled = false;
            WeaponView.Clear();
            return;
        }

        bool aiming = local.State.HasFlag(PlayerStateFlags.Aiming);
        bool pullingGrenade = Object.Item.Type == ItemType.Grenade
            && local.State.HasFlag(PlayerStateFlags.Shooting);
        float modelScale = ItemCosmetics.FirstPersonScale(Object.Item.Type);

        // Sprinting is just "Shift is down", so it has to be qualified: standing still with Shift
        // held is not a run, and a sprint pose while aiming would fight the sight picture.
        bool sprinting = local.State.HasFlag(PlayerStateFlags.Sprinting)
                         && local.State.HasFlag(PlayerStateFlags.Moving)
                         && !aiming
                         && !pullingGrenade;
        sprintBlend = MathUtil.Lerp(
            sprintBlend,
            sprinting ? 1f : 0f,
            1f - MathF.Exp(-SprintBlendSharpness * dt));
        if (sprintBlend > 0.001f)
            swayPhase += dt * SwayHz * MathUtil.TwoPi;

        var targetGripOffset = pullingGrenade
            ? WeaponMount.GrenadePullbackGripOffset
            : Mount.FirstPersonGripOffset(Object.Item.Type, aiming, modelScale)
              + WeaponMount.SprintGripDelta * sprintBlend;

        if (firstViewFrame)
        {
            viewGripOffset = targetGripOffset;
            firstViewFrame = false;
        }
        else
        {
            // The weapon's own aim speed scales the rate, so a heavy gun takes proportionally longer
            // to come up — and the same scale paces the camera's field of view in FirstPersonCamera,
            // so the sight picture and the view arrive together.
            viewGripOffset = Vector3.Lerp(viewGripOffset, targetGripOffset,
                1f - MathF.Exp(-ViewModelSharpness * ItemCosmetics.AimSpeedScale(Object.Item.Type) * dt));
        }

        UpdateRecoil(dt, aiming);

        var cameraRotation = CameraEntity.Transform.Rotation;
        var weaponRotation = WeaponMount.FirstPersonRotationFor(Object.Item.Type, swing.Angle);
        var modelOffset = Mount.FirstPersonModelOffset(Object.Item.Type, viewGripOffset, modelScale).ToStride();
        var muzzleOffset = Mount.FirstPersonMuzzleOffset(Object.Item.Type, viewGripOffset, modelScale).ToStride();
        // Everything that displaces the view model — recoil, the swing's reach, the sprint sway —
        // is summed into ONE camera-space offset and ONE camera-space rotation, then applied to the
        // model and to the muzzle delta alike. That is what keeps the shot leaving the barrel the
        // player can see however many of them are running at once.
        float sway = MathF.Sin(swayPhase);
        var viewOffset = new Vector3(0f, recoilLift, recoilBack)
            + SwingReach * MathF.Max(0f, swing.Angle)
            + new Vector3(SwaySide * sway, SwayRise * MathF.Sin(swayPhase * 2f), 0f) * sprintBlend;
        var viewRotation = Quaternion.RotationX(recoilPitch - WeaponMount.SprintPitch * sprintBlend)
            * Quaternion.RotationZ(SwayRoll * sway * sprintBlend);

        var displacedModelOffset = modelOffset + viewOffset;
        var displacedMuzzleOffset = displacedModelOffset
            + Vector3.Transform(muzzleOffset - modelOffset, viewRotation);

        Entity.Transform.Scale = new Vector3(modelScale);
        Entity.Transform.Position = CameraEntity.Transform.Position
            + Vector3.Transform(displacedModelOffset, cameraRotation);
        Entity.Transform.Rotation = weaponRotation.ToStride() * viewRotation * cameraRotation;

        // Only a weapon supplies the sim's shot origin; a shovel has no muzzle to report.
        if (!Object.Has.HasFlag(NetComponents.Weapon))
        {
            if (WeaponView.NetworkId == Object.NetworkId) WeaponView.Clear();
            return;
        }

        WeaponView.MuzzleWorld = (System.Numerics.Vector3)(
            CameraEntity.Transform.Position
            + Vector3.Transform(displacedMuzzleOffset, cameraRotation));
        WeaponView.Weapon = Object.Item.Type;
        WeaponView.NetworkId = Object.NetworkId;
    }

    private void UpdateRecoil(float dt, bool aiming)
    {
        if (!Object.Has.HasFlag(NetComponents.Weapon) || Object.Item.Type == ItemType.Grenade) return;

        float recovery = MathF.Exp(-RecoilReturnSharpness * dt);
        recoilBack *= recovery;
        recoilLift *= recovery;
        recoilPitch *= recovery;

        int shots = shotsThisFrame;
        if (shots == 0) return;

        var kick = RecoilFor(Object.Item.Type);
        if (aiming && Object.Item.Type == ItemType.Ppsh)
        {
            const float PpshAdsKickScale = 0.30f;
            kick = new RecoilKick(
                kick.Back * PpshAdsKickScale,
                kick.Lift * PpshAdsKickScale,
                kick.PitchRadians * PpshAdsKickScale);
        }
        recoilBack = MathF.Min(MaxRecoilBack, recoilBack + kick.Back * shots);
        recoilLift = MathF.Min(MaxRecoilLift, recoilLift + kick.Lift * shots);
        recoilPitch = MathF.Min(MaxRecoilPitch, recoilPitch + kick.PitchRadians * shots);
    }

    private static RecoilKick RecoilFor(ItemType type) => type switch
    {
        ItemType.AWP or ItemType.Mosin => new RecoilKick(0.10f, 0.020f, MathUtil.DegreesToRadians(6f)),
        ItemType.Glock or ItemType.Ppsh => new RecoilKick(0.040f, 0.008f, MathUtil.DegreesToRadians(3.5f)),
        _ => new RecoilKick(0.050f, 0.010f, MathUtil.DegreesToRadians(2.5f)),
    };
}
