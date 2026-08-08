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
    private string? handNodeName;
    private int handNode = -1;
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
        // One body per id — PlayerRegistry ignores a repeat spawn, so this cannot be ambiguous. When
        // it could, the oldest match won and every weapon ended up on an orphaned body.
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

        // Hands off his own kit: hauling something, or working an emplacement. Either way nothing
        // he owns is drawn — not the selected weapon in first person, not the slung rifle in third.
        // Asked of the OWNER rather than of the item, so one carried thing or one mortar hides
        // everything else he has at once.
        bool handsBusy = (player.IsCarrying && Object.Attachment.Slot != EquipSlot.Carried)
                         || player.IsOperating;
        if (handsBusy)
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

        // Two mounts, and which one is right depends on what the man is doing.
        //
        // AIMING: the weapon is the thing being pointed, so it takes its orientation from the actor
        // and ignores the arm — see SeatInHand.
        //
        // NOT AIMING: the weapon is being CARRIED, and the carry is the animation's to describe.
        // Hanging it off the hand bone the old way is what makes it swing with the arms at a run,
        // and no amount of actor-derived orientation reproduces that, because the sway is not in
        // the actor's transform at all — it only exists in the pose.
        //
        // The switch is instant, like the Aiming clip's own (PlayerViewScript blends it with no
        // fade), and the two agree closely at that moment anyway: HandRotation was tuned so the
        // firing pose points the barrel forward, which is where the aimed mount puts it.
        var socket = ItemCosmetics.GetSocket(Object.Attachment.Slot, Object.Item.Type, Mount);
        if (socket.Node is { } hand && player.State.HasFlag(PlayerStateFlags.Aiming))
            SeatInHand(hand, player.Pitch);
        else
            Seat(socket with
            {
                Rotation = WeaponMount.HandRotationFor(Object.Item.Type, swing.Angle).ToStride(),
            });
    }

    /// <summary>
    /// A held item takes its POSITION from the hand and its ORIENTATION from the actor: the grip
    /// lands on the hand bone, and the weapon points where the man is aiming — his yaw and his
    /// pitch, upright about the barrel — whatever the arm underneath it happens to be doing.
    ///
    /// That split is why this does not use <see cref="ModelNodeLinkComponent"/> — a bone link
    /// supplies the whole parent matrix, so anything hung off it inherits the bone's rotation by
    /// construction and the only way out is to cancel that rotation back out again. Reading the one
    /// thing we want from the skeleton is both shorter and honest about which frame each half of
    /// the transform comes from.
    ///
    /// The entity stays at the scene root, so what is written here IS the world transform.
    /// </summary>
    private void SeatInHand(string node, float pitch)
    {
        if (linkedNode != null)
        {
            Entity.Remove<ModelNodeLinkComponent>();
            // ModelNodeLinkProcessor only clears the link in Draw, one phase after this, and until
            // it does the transform written below would be composed onto the bone as if it were
            // still bone-local — a one-frame jump every time the man raises his sights.
            Entity.Transform.TransformLink = null;
            linkedNode = null;
        }

        var type = Object.Item.Type;

        // Three rotations, innermost frame first, because Stride and System.Numerics both compose
        // as "apply a, THEN b":
        //   the item's own swing, in its model frame, so a chop still reads as a chop;
        //   the aim pitch, about the actor's X — the same PitchRotation the aim bone and the shot
        //     both use, so the drawn barrel agrees with where the man is looking;
        //   the actor's yaw, which is the whole of his transform (PlayerViewScript writes
        //     RotationY(Yaw) and nothing else, for players and NPCs alike), taking it into world.
        // Nothing here reads the arm, which is the point: no wrist twist, no roll.
        var rotation = System.Numerics.Quaternion.Concatenate(
            WeaponMount.SwingRotation(swing.Angle),
            System.Numerics.Quaternion.Concatenate(
                WeaponMount.PitchRotation(pitch),
                owner!.Transform.Rotation.ToNumerics()));

        // Seat is the offset that lands the model's grip on a point, solved for THIS rotation; the
        // scale it is drawn at has to go through it too, or a model drawn at anything but 1:1 hangs
        // off its hand by the fraction it was shrunk (the shovel, at 0.2).
        float scale = ItemCosmetics.WorldScale(type);
        Entity.Transform.Rotation = rotation.ToStride();
        Entity.Transform.Position =
            (HandPosition(node) + Mount.Seat(type, rotation) * scale).ToStride();
    }

    /// <summary>
    /// Where the hand bone is, in world space, this frame.
    ///
    /// Skeleton world matrices are computed in Draw, so what is readable from a script is the
    /// previous frame's pose — and <see cref="TransformComponent.WorldMatrix"/> is one frame behind
    /// for the same reason. That pairing is what makes this exact where it matters: the bone offset
    /// is taken in the owner's OWN frame from those two stale-but-consistent values, then placed by
    /// the owner's CURRENT transform. Only the animation pose is a frame old. Using the bone's world
    /// position directly would leave a sprinting man's rifle trailing a stride behind him.
    /// </summary>
    private System.Numerics.Vector3 HandPosition(string node)
    {
        var transform = owner!.Transform;
        var current = transform.Position.ToNumerics();
        if (owner.Get<ModelComponent>()?.Skeleton is not { } skeleton) return current;

        if (handNodeName != node)
        {
            // A name that is not in the rig resolves to -1 and stays there: the item sits at the
            // actor's origin, which is wrong but findable, rather than silently at the world's.
            handNode = Array.FindIndex(skeleton.Nodes, bone => bone.Name == node);
            handNodeName = node;
        }
        if (handNode < 0) return current;

        var bone = skeleton.NodeTransformations[handNode].WorldMatrix.TranslationVector;
        var posed = transform.WorldMatrix;
        posed.Decompose(out _, out Quaternion posedRotation, out Vector3 posedPosition);

        var inOwnerFrame = System.Numerics.Vector3.Transform(
            (bone - posedPosition).ToNumerics(),
            System.Numerics.Quaternion.Inverse(posedRotation.ToNumerics()));
        return current
            + System.Numerics.Vector3.Transform(inOwnerFrame, transform.Rotation.ToNumerics());
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
            || ItemCosmetics.StowedSocket(hotbar, Object.Item.Type, Mount) is not { } socket)
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
    /// <summary>
    /// Whether this is the local player's own item, drawn as a view model rather than on a body he
    /// cannot see. Carried counts: something hauled in both hands is the most first-person thing
    /// there is, and leaving it out is what made a picked-up mortar invisible to its own carrier —
    /// it was being seated on the torso bone of a disabled model.
    /// </summary>
    private bool IsLocalViewModel()
        => Registry.LocalPlayer is { } local
           && Object.Owner.PlayerId == local.Id
           && (Object.Attachment.Slot is EquipSlot.Hand or EquipSlot.Carried
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
        //
        // Never for something hauled. Every view-model animation there is — the sway, the roll, the
        // sprint pitch — is scaled by sprintBlend, so refusing it here is what makes a carried thing
        // sit dead still in the hands while its owner moves. It is the right shape for one too: the
        // sway is a weapon swinging in one hand at a run, and a mortar is clamped against the chest
        // with both.
        bool sprinting = local.State.HasFlag(PlayerStateFlags.Sprinting)
                         && local.State.HasFlag(PlayerStateFlags.Moving)
                         && !aiming
                         && !pullingGrenade
                         && !ItemConfig.IsCarryable(Object.Item.Type);
        sprintBlend = MathUtil.Lerp(
            sprintBlend,
            sprinting ? 1f : 0f,
            1f - MathF.Exp(-SprintBlendSharpness * dt));
        if (sprintBlend > 0.001f)
            swayPhase += dt * SwayHz * MathUtil.TwoPi;

        // Hauled in front of the chest in both hands, and none of the weapon poses apply: there is
        // no aiming it, no sprint carry, no sights to come up to. It just sits there in the way,
        // which is the point of it.
        var targetGripOffset = ItemConfig.IsCarryable(Object.Item.Type)
            ? WeaponMount.CarriedGripOffset
            : pullingGrenade
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

        UpdateRecoil(dt, aiming, local.State.HasFlag(PlayerStateFlags.Prone));

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

    private void UpdateRecoil(float dt, bool aiming, bool prone)
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
        if (prone)
        {
            kick = new RecoilKick(
                kick.Back * BallisticsConfig.ProneRecoilScale,
                kick.Lift * BallisticsConfig.ProneRecoilScale,
                kick.PitchRadians * BallisticsConfig.ProneRecoilScale);
        }
        float capScale = prone ? BallisticsConfig.ProneRecoilScale : 1f;
        recoilBack = MathF.Min(MaxRecoilBack * capScale, recoilBack + kick.Back * shots);
        recoilLift = MathF.Min(MaxRecoilLift * capScale, recoilLift + kick.Lift * shots);
        recoilPitch = MathF.Min(MaxRecoilPitch * capScale, recoilPitch + kick.PitchRadians * shots);
    }

    private static RecoilKick RecoilFor(ItemType type) => type switch
    {
        ItemType.AWP or ItemType.Mosin => new RecoilKick(0.10f, 0.020f, MathUtil.DegreesToRadians(6f)),
        ItemType.Glock or ItemType.Ppsh => new RecoilKick(0.040f, 0.008f, MathUtil.DegreesToRadians(3.5f)),
        _ => new RecoilKick(0.050f, 0.010f, MathUtil.DegreesToRadians(2.5f)),
    };
}

/// <summary>
/// The white trail behind a mortar bomb.
///
/// Drawn from where the bomb has BEEN rather than from a predicted arc, so it shows the flight that
/// actually happened — including the scatter, which a predicted line drawn to the aim point would
/// quietly hide. The entity's transform is driven by NetTransformScript off the replicated
/// position, so sampling it once a frame is sampling the real thing.
/// </summary>
public sealed class MortarRoundScript : SyncScript
{
    /// <summary>How many samples the trail keeps. At 60 fps this is about a second and a half of
    /// flight, which is enough to read as an arc without drawing the whole parabola.</summary>
    private const int MaxSamples = 90;

    /// <summary>Metres between samples. Without it a stationary frame would fill the buffer with
    /// duplicates of one point.</summary>
    private const float MinimumStep = 0.35f;

    private static readonly Color TrailColor = new(250, 250, 250, 215);

    private readonly List<Stride.Core.Mathematics.Vector3> trail = [];

    public override void Update()
    {
        var here = Entity.Transform.Position;
        if (trail.Count == 0
            || Stride.Core.Mathematics.Vector3.Distance(trail[^1], here) >= MinimumStep)
        {
            trail.Add(here);
            if (trail.Count > MaxSamples) trail.RemoveAt(0);
        }

        // The live position closes the line every frame, so the trail stays attached to the bomb
        // between samples instead of lagging up to MinimumStep behind it.
        if (trail.Count < 2) return;
        LineRenderer.DrawPolyline(trail, TrailColor);
        LineRenderer.DrawLine(trail[^1], here, TrailColor);
    }
}
