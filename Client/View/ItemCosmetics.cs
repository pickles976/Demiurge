using Demiurge;
using Demiurge.GameClient;
using Stride.Core.Mathematics;

// Client-only item cosmetics, keyed by the same ItemType as ItemConfig.
// Kept out of Common so the wire protocol and the server stay view-free.
// Weapon shot effects (sound, tracer) live in WeaponFx.
//
// WHICH BONE a worn item hangs off, and how it is ORIENTED there, comes from the
// SLOT (replicated in AttachmentState): every Hand item points like a gun, every
// Chest item lies like armor. A socket's Node is a bone name (from the rig — dump
// the gltf's node names if unsure) linked via ModelNodeLinkComponent; null follows
// the Player_{id} entity root instead.
//
// HOW FAR ALONG the item sits is the one thing that comes from the ITEM, because
// it depends on where that model's author put its origin. It is read from the
// model's `grip` locator rather than tuned by hand — see SeatFor.
public static class ItemCosmetics
{
    public readonly record struct Socket(string? Node, Vector3 Seat, Quaternion Rotation);

    public static string Model(ItemType type) => type switch
    {
        ItemType.Ak47 => "assets/models/ak47.gltf",
        ItemType.Sks => "assets/models/sks.gltf",
        ItemType.Ppsh => "assets/models/ppsh.gltf",
        ItemType.Mosin => "assets/models/mosin.gltf",
        ItemType.Dp27 => "assets/models/dp_27.gltf",
        ItemType.AWP => "assets/models/sniper_rifle.gltf",
        ItemType.Glock => "assets/models/glock.gltf",
        ItemType.Shovel => "assets/models/shovel.gltf",
        ItemType.BodyArmor => "assets/models/body_armor.gltf",
        ItemType.Grenade => "assets/models/grenade.gltf",

        // Unknown type off the wire: AK stand-in rather than a crash.
        _ => "assets/models/ak47.gltf",
    };

    /// <summary>
    /// Map-authored weapon supply, drawn as the crate rather than as the weapon inside it. One
    /// model for every item, because the crate is the point: you learn where resupply is, not what
    /// is in it, until you walk over it. The server marks these with
    /// <see cref="ObjectType.Crate"/>, which only a world pickup ever carries.
    /// </summary>
    public const string SupplyCrateModel = "assets/models/crate.gltf";

    /// <summary>
    /// Model units to world units, for a pickup lying on the ground and for a worn item on
    /// somebody else's body. One per model rather than one global number because the models were
    /// not authored to a shared scale: the rifles come out at about a metre and the shovel comes
    /// out at 2.26, which reads as a pike rather than an entrenching tool.
    /// </summary>
    public static float WorldScale(ItemType type) => type switch
    {
        // The shovel is modelled at 2.26 units end to end, which is a pike. Eyeballed against the
        // rig rather than against the rifles: matching the AK's 0.93 was the obvious guess and came
        // out twice too big, because the AK is itself drawn large next to this character.
        ItemType.Shovel => 0.2f,
        _ => 1f,
    };

    /// <summary>
    /// The view model is drawn larger than the world model — the usual first-person cheat, so the
    /// gun in your hands reads at arm's length. Derived from <see cref="WorldScale"/> so an item
    /// only ever needs sizing once.
    /// </summary>
    public static float FirstPersonScale(ItemType type)
        => type == ItemType.Grenade
            ? WorldScale(type)
            : WorldScale(type) * WeaponMount.FirstPersonScale;

    /// <summary>
    /// How much closer aiming this weapon pulls the world, ON TOP of the ADS view every weapon
    /// gets. 1 is the ordinary sight picture; 2 is twice the magnification of that, not twice the
    /// magnification of the hip view.
    ///
    /// A ratio rather than a per-weapon field of view, because the number worth tuning per weapon
    /// is "how much glass is on it" — the shared ADS framing stays one number in FirstPersonCamera
    /// and moving it re-frames every optic at once instead of drifting away from a table of angles.
    ///
    /// Presentation only. The shot is sampled from the aim direction and the weapon's MOA, so a
    /// scope makes a target easier for the PLAYER to lay the reticle on and buys no accuracy; the
    /// reticle's own bloom already reads the live field of view and follows this for free.
    /// </summary>
    public static float AimMagnification(ItemType type) => type switch
    {
        ItemType.Mosin => 2f,
        _ => 1f,
    };

    /// <summary>
    /// How fast this weapon comes up to the sights, as a multiple of the ordinary rate — 0.7 being
    /// "30% slower to aim". It scales the two exponential blends that ARE the ADS animation: the
    /// view model travelling from the hip grip to the sight grip, and the camera settling onto the
    /// aimed field of view. Both, because either one alone reads as a bug — the gun arriving before
    /// the view or the other way round.
    ///
    /// Presentation, like <see cref="AimMagnification"/>. Nothing gates a shot on the sight picture
    /// having arrived; what the weight of a weapon costs in the SIM is
    /// <see cref="WeaponStats.MoveSpeedScale"/>.
    /// </summary>
    public static float AimSpeedScale(ItemType type) => type switch
    {
        // Nine kilos with a pan magazine on top of it. The same 0.7 its movement carries, because
        // it is the same weight doing both.
        ItemType.Dp27 => 0.7f,
        _ => 1f,
    };

    // A new EquipSlot needs a row here — that's the whole client cost of a slot.
    // Seats are the FALLBACK for models with no grip locator; a Hand item's real seat
    // comes from WeaponMount, which owns it so the sim's muzzle can be derived from the
    // same numbers. Head and Back are untuned placeholders: eyeball them when the first
    // helmet/backpack lands.
    private static readonly Dictionary<EquipSlot, Socket> SlotSockets = new()
    {
        [EquipSlot.Hand] = new("right_hand", Vector3.Zero, WeaponMount.HandRotation.ToStride()),
        [EquipSlot.Chest] = new("torso", Vector3.Zero, Quaternion.Identity),
        [EquipSlot.Head] = new("head", Vector3.Zero, Quaternion.Identity),
        [EquipSlot.Back] = new("torso", new Vector3(0f, 0f, -0.2f), Quaternion.Identity),
        [EquipSlot.HotbarPrimary] = new("right_hand", Vector3.Zero, WeaponMount.HandRotation.ToStride()),
        [EquipSlot.HotbarShovel] = new("right_hand", Vector3.Zero, WeaponMount.HandRotation.ToStride()),
        [EquipSlot.HotbarGrenade] = new("right_hand", Vector3.Zero, WeaponMount.HandRotation.ToStride()),
    };

    // Unknown slot off the wire: ride the player root rather than crash.
    public static Socket GetSocket(EquipSlot slot, ItemType type, WeaponMount mount)
    {
        if (!SlotSockets.TryGetValue(slot, out var socket))
            return new Socket(null, Vector3.Zero, Quaternion.Identity);

        return slot == EquipSlot.Hand || HotbarConfig.TryFromStorageSlot(slot, out _)
            ? socket with
            {
                Seat = mount.Seat(type).ToStride(),
                Rotation = WeaponMount.HandRotationFor(type).ToStride(),
            }
            : socket;
    }

    // ---- Stowed kit -------------------------------------------------------------------------
    //
    // A hotbar item you are NOT holding used to be hidden outright. It is worn instead: the rifle
    // across the back, the shovel on the left hip. That is purely a client decision — the item is
    // still in its hotbar slot on the wire, and nothing about ownership or occupancy changes — so
    // it lives here beside the held sockets rather than costing an EquipSlot.
    //
    // These are measured in BONE space, which for `torso` and `upper_chest` is the model root's
    // frame: the rig leaves both at identity rotation in its rest pose. In that frame +Y is up,
    // +Z is the way the character faces, and +X is the character's LEFT (the rig puts
    // left_shoulder at +X). Get that last one backwards and the shovel hangs off the wrong hip.

    /// <summary>The angle the two back-carried pieces lean, in opposite directions, so they cross
    /// instead of occupying the same line.</summary>
    private static readonly float BackLean = MathUtil.DegreesToRadians(24f);

    /// <summary>
    /// A quarter turn about the weapon's OWN forward axis — model +Z, the barrel — so the receiver
    /// lies flat against the back rather than edge-on to it.
    ///
    /// Applied FIRST, which is what makes it a roll along the barrel instead of a swing across the
    /// spine: Stride multiplies as "apply a, then b", so a rotation composed before the slinging
    /// happens in the model's own frame, where +Z still points down the barrel. Compose it after and
    /// the same quarter turn is taken about whatever axis the barrel has already been laid onto.
    /// </summary>
    private static readonly float SlungRoll = MathUtil.PiOverTwo;

    /// <summary>Barrel up the spine and sights facing away from the back: model +Z (muzzle) onto
    /// world +Y, model +Y (top of the receiver) onto world -Z. Then leaned so the rifle lies
    /// diagonally rather than straight up the middle.</summary>
    private static readonly Quaternion SlungRotation =
        Quaternion.RotationZ(SlungRoll)
        * Quaternion.RotationX(-MathUtil.PiOverTwo)
        * Quaternion.RotationZ(BackLean);

    /// <summary>
    /// The point on the back that stowed kit hangs from, in `torso` bone space.
    ///
    /// One constant for both pieces because they are meant to cross AT it — leaned opposite ways
    /// about a shared point rather than parked at two positions that have to be re-tuned in step
    /// every time either moves.
    /// </summary>
    private static readonly Vector3 BackAnchor = new(0.1f, 0.42f, -0.14f);

    /// <summary>Blade down and leaned the other way. The shovel is modelled standing up its own
    /// shaft, so turning it over is the whole job.</summary>
    private static readonly Quaternion HangingRotation =
        Quaternion.RotationX(MathF.PI) * Quaternion.RotationZ(-BackLean);

    /// <summary>
    /// Where a hotbar item rides while a different slot is selected, or null for kit that is not
    /// drawn when stowed. Grenades are the null case: a belt pouch is not modelled, and floating
    /// a stick grenade beside the hip would read as a bug.
    ///
    /// Rifle and shovel both ride the back, leaned opposite ways and seated at different heights so
    /// they cross rather than intersect.
    /// </summary>
    public static Socket? StowedSocket(HotbarSlot slot, ItemType type, WeaponMount mount) => slot switch
    {
        // Slung across the back, hung from its GRIP rather than from wherever the model's author put
        // the origin — which is what lets one anchor serve weapons of different lengths without each
        // one needing its own seat. `torso` is the pelvis in this rig, so both pieces stay put
        // instead of swinging with the walk cycle.
        HotbarSlot.Primary => new Socket("torso", GripAt(BackAnchor, type, SlungRotation, mount), SlungRotation),

        // Seated by its origin, which is where it was tuned. The shovel is modelled standing up its
        // own shaft, so its origin already sits about where it should hang from.
        HotbarSlot.Shovel => new Socket("torso", BackAnchor, HangingRotation),

        _ => null,
    };

    /// <summary>
    /// The seat that lands a model's `grip` locator on <paramref name="point"/>.
    ///
    /// A model-space point p is drawn at `p * scale * rotation + seat`, so putting the grip at a
    /// chosen spot is just that equation solved for the seat. <see cref="WeaponMount.Seat(ItemType,
    /// System.Numerics.Quaternion)"/> is the same solve for the origin case, which is why this is
    /// its result offset by the target rather than a second copy of the rotation maths.
    ///
    /// Falls back to seating by the origin for a model with no grip locator — the item sits in
    /// roughly the right place rather than vanishing to the bone.
    /// </summary>
    private static Vector3 GripAt(Vector3 point, ItemType type, Quaternion rotation, WeaponMount mount)
        => point + mount.Seat(type, rotation.ToNumerics()).ToStride() * WorldScale(type);
}
