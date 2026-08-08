using System.Numerics;
using Demiurge;

namespace Demiurge.GameClient
{
    /// <summary>
    /// Where a weapon sits on the body, and where its barrel therefore ends up.
    ///
    /// Both answers live here because they must not drift: the seat is what the renderer
    /// uses to place the model, the muzzle is what the sim uses as a shot's origin, and
    /// if they are derived from two copies of the same constants then bullets stop coming
    /// out of the gun you can see. One source, two readers.
    ///
    /// This sits in Core rather than View because the sim reads it. Nothing here is a
    /// live view fact — it is all measured off the models at build time, so reading it
    /// from the sim is not a View -> Sim write, and the one-way flow holds.
    /// </summary>
    public sealed class WeaponMount
    {
        /// <summary>
        /// The rig every player wears; the hand a weapon hangs off is on it.
        ///
        /// One model, literally: teams differ by the texture PlayerCosmetics hangs on this rig, so
        /// every measurement taken here holds for all of them. A team whose rig actually differed
        /// would need its own numbers, not a second constant here.
        ///
        /// Also the key this file's locators are looked up under — see ModelLocators, which keys by
        /// content path — so renaming the asset moves the measurements with it.
        /// </summary>
        public const string PlayerModel = "assets/models/cat.gltf";

        private const string HandBone = "right_hand";

        /// <summary>The bone the aim pitch is applied to. It parents the neck AND both shoulders, so
        /// rotating it swings head, arms, hands and — through the hand's ModelNodeLinkComponent —
        /// the gun, all from one override. Everything below pivots about it, which is why the muzzle
        /// has to pivot about it too.</summary>
        public const string AimBone = "upper_chest";

        /// <summary>The stable pose used to measure the body-attached muzzle. This clip holds
        /// the arm still — its hand transform is constant to four decimals across the whole
        /// clip — so one baked pose is exact here rather than an average of a moving arm.
        /// First-person hip fire uses the camera-relative view-model muzzle instead.</summary>
        private const string FiringPose = "Aiming";

        /// <summary>Which way a gun points once it is in the hand. Hand-tuned, and it has
        /// to be: Blockbench locators carry a position but no orientation, so a `grip`
        /// locator can say where the hand grips and not which way the barrel then faces.
        ///
        /// Built to match Stride's RotationX(90) * RotationZ(180) — "apply X, then Z",
        /// Stride's multiply order being the reverse of the usual one. As a basis that is
        /// (x,y,z) -> (-x, z, y), which is what to check it against if it ever moves.</summary>
        public static readonly Quaternion HandRotation = Quaternion.Concatenate(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI));

        /// <summary>Model space to hand-bone space, with an optional <see cref="Pitch"/>.</summary>
        public static Quaternion HandRotationFor(ItemType type, float pitch = 0f)
            => Quaternion.Concatenate(Pitch(pitch), HandRotation);

        /// <summary>
        /// Model space to camera space, with the item's view-model rest turn and an optional
        /// <see cref="Pitch"/>. The rest turn is baked in here rather than applied at the renderer,
        /// so the seat and muzzle offsets derived from this rotation stay attached to the model the
        /// player is actually looking at.
        /// </summary>
        public static Quaternion FirstPersonRotationFor(ItemType type, float pitch = 0f)
            => Quaternion.Concatenate(
                FirstPersonRestRotation(type),
                Quaternion.Concatenate(Pitch(pitch), FirstPersonRotation));

        /// <summary>
        /// An extra rotation applied in the item's OWN model frame, about the axis every model here
        /// swings around: +X, the one perpendicular to both "up" (+Y) and "the way it points". A gun
        /// pitches its muzzle about it and a shovel chops about it, which is why one function serves
        /// both and why a held-item animation reads the same in first and third person.
        /// </summary>
        private static Quaternion Pitch(float radians)
            => radians == 0f
                ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(Vector3.UnitX, radians);

        /// <summary>
        /// <see cref="Pitch"/> on its own, for a caller that orients an item in some frame OTHER
        /// than the hand bone's and so wants the swing without <see cref="HandRotation"/> baked in.
        /// The third-person held item is that caller: it takes its facing from the actor, not from
        /// the arm.
        /// </summary>
        public static Quaternion SwingRotation(float radians) => Pitch(radians);

        /// <summary>
        /// First-person view-model orientation. <see cref="HandRotation"/> is only half of the
        /// third-person chain: it lives under the animated right_hand bone, whose aiming pose swings
        /// the barrel forward. A camera-relative model has no hand bone, so this applies that missing
        /// quarter-turn directly: model +Z (barrel) ends up on camera -Z, and model +Y remains up.
        /// </summary>
        public static readonly Quaternion FirstPersonRotation = Quaternion.Concatenate(
            HandRotation,
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f));

        public const float FirstPersonScale = 1.5f;
        public static readonly Vector3 HipGripOffset = new(0.30f, -0.34f, -0.38f);
        public static readonly Vector3 AimGripOffset = new(0f, -0.22f, -0.30f);
        public static readonly Vector3 GlockAimGripOffset = new(0f, -0.22f, -0.55f);
        public static readonly Vector3 GrenadePullbackGripOffset = new(0.42f, -0.30f, -0.10f);

        /// <summary>
        /// How far in front of the eye the REAR sight sits while aiming — the only ADS number left
        /// to tune on a weapon that carries sight locators, because everything else about where the
        /// gun goes is then solved rather than eyeballed. Chosen to keep the eye relief the
        /// hand-tuned <see cref="AimGripOffset"/> already had, so switching a weapon onto sights
        /// changes its alignment and not its distance.
        /// </summary>
        public const float AimSightRelief = 0.65f;

        /// <summary>
        /// Where a TOOL sits, aimed or not. It is carried lower than a weapon because it is held
        /// rather than shouldered, and it does not move on right-click: a shovel has nothing to
        /// aim, so letting it snap to a firing position would just be the ADS animation playing on
        /// something that cannot shoot.
        /// </summary>
        public static readonly Vector3 ToolGripOffset = new(0.42f, -0.49f, -0.55f);

        /// <summary>
        /// Where a held item moves to while sprinting, as a delta on whatever its resting position
        /// is rather than an absolute. A delta because the shovel is already carried much lower
        /// than a rifle, and "drop it out of the way and pull it in" is the same gesture for both —
        /// an absolute sprint pose would have to be re-tuned for every item that gets its own
        /// carry position.
        /// </summary>
        public static readonly Vector3 SprintGripDelta = new(0.02f, -0.16f, 0.06f);

        /// <summary>How far the muzzle drops while sprinting, radians, applied about the CAMERA's
        /// right axis so it composes with recoil and reaches the muzzle origin too.</summary>
        public const float SprintPitch = 0.42f;

        /// <summary>
        /// Camera-relative grip position while hip-firing. ADS goes through
        /// <see cref="FirstPersonGripOffset(ItemType, bool, float)"/>, which prefers a weapon's own
        /// sights when it has them.
        /// </summary>
        public static Vector3 HipFireGripOffset(ItemType type)
            => IsTool(type) ? ToolGripOffset : HipGripOffset;

        /// <summary>
        /// The hand-tuned ADS position, used by weapons with no sight locators. The compact Glock
        /// needs extra eye relief; using this one entry point keeps its rendered model and muzzle
        /// origin together.
        /// </summary>
        public static Vector3 FallbackAimGripOffset(ItemType type)
            => IsTool(type) ? ToolGripOffset
             : type == ItemType.Glock ? GlockAimGripOffset
             : AimGripOffset;

        /// <summary>Held, but not a gun — the weapon trait table is what says so.</summary>
        private static bool IsTool(ItemType type) => WeaponConfig.Get(type) is null;

        /// <summary>
        /// How a held item is turned in the VIEW MODEL before any animation is added.
        ///
        /// About the item's own +Y — its VERTICAL axis, the one the shovel's shaft runs up — so the
        /// blade turns to face across the view instead of edge-on to it. That is a different axis
        /// from <see cref="Pitch"/>, which tips the item forward; a shovel wants the first and the
        /// swing wants the second, and they compose in that order so the chop stays in the same
        /// plane whichever way the blade is turned.
        ///
        /// First person only. Third person keeps the item square to the hand bone, whose animation
        /// is already posing the arm.
        /// </summary>
        public static Quaternion FirstPersonRestRotation(ItemType type)
            => type == ItemType.Shovel
                ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 3f)   // -60 degrees
                : Quaternion.Identity;

        private readonly ModelLocators locators;
        private readonly Func<ItemType, string> modelOf;
        private readonly ModelLocators.Pose firingHand;
        private readonly Vector3 aimPivot;

        public WeaponMount(ModelLocators locators, Func<ItemType, string> modelOf)
        {
            this.locators = locators;
            this.modelOf = modelOf;
            firingHand = locators.Require(PlayerModel, HandBone, FiringPose);
            aimPivot = locators.Require(PlayerModel, AimBone, FiringPose).Translation;
        }

        /// <summary>
        /// Camera-relative grip position, hip or aimed.
        ///
        /// Aiming is DERIVED when the model carries `rear_sight` and `front_sight` locators: put the
        /// grip wherever it has to go for the line through the two sights to pass through the eye,
        /// which is what "aiming down the sights" physically is. That makes the alignment a property
        /// of the model rather than of three numbers somebody nudged until it looked right, and it
        /// survives the artist moving the sights.
        ///
        /// Weapons without those locators fall back to the hand-tuned offsets, so this is additive:
        /// only the SKS has sights today and only the SKS changes.
        /// </summary>
        public Vector3 FirstPersonGripOffset(ItemType type, bool aiming, float scale = FirstPersonScale)
            => aiming
                ? SightGripOffset(type, scale) ?? FallbackAimGripOffset(type)
                : HipFireGripOffset(type);

        /// <summary>
        /// Where the grip must sit, in camera space, for this weapon's sight line to run through the
        /// eye — null for a model that has no sights to align.
        ///
        /// Solved rather than searched. The two sights are rigidly attached to the grip, so their
        /// camera-space separation `axis` is fixed once the model's rotation and scale are known.
        /// Any point of the form `t * normalize(axis)` lies on the ray leaving the eye along the
        /// sight direction, so seating the REAR sight there puts both sights — and therefore the
        /// whole line between them — on a line through the eye. `t` is the eye relief and the only
        /// free parameter left.
        /// </summary>
        public Vector3? SightGripOffset(ItemType type, float scale = FirstPersonScale)
        {
            var model = modelOf(type);
            if (locators.Get(model, "grip") is not { } grip
                || locators.Get(model, "rear_sight") is not { } rear
                || locators.Get(model, "front_sight") is not { } front)
                return null;

            var rotation = FirstPersonRotationFor(type);
            var axis = Vector3.Transform((front.Translation - rear.Translation) * scale, rotation);
            if (axis.LengthSquared() < 1e-8f) return null;   // sights on top of each other: no line

            var rearFromGrip = Vector3.Transform((rear.Translation - grip.Translation) * scale, rotation);
            return AimSightRelief * Vector3.Normalize(axis) - rearFromGrip;
        }

        /// <summary>
        /// The aim pitch as a rotation in the player's own space, positive being up.
        ///
        /// NEGATIVE about X: a rotation of +theta about X carries +Z (the way the character faces)
        /// toward -Y, which would drop the barrel while the player looks up.
        ///
        /// This is only a player-space rotation because the aim bone's parents are unrotated in the
        /// firing pose — the Aiming clip animates arms only, leaving torso and upper_chest at
        /// identity — so the bone's local frame and the player's coincide. A clip that leaned the
        /// torso would break that equivalence, and the muzzle would drift off the drawn barrel.
        /// </summary>
        public static Quaternion PitchRotation(float pitch) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, -pitch);

        /// <summary>Where to put the model relative to the hand bone so that its GRIP
        /// lands on the bone, whatever its author chose as the model origin — the thing
        /// that replaces a hand-tuned per-slot constant.
        ///
        /// ModelNodeLinkComponent makes the bone the parent, so a model-space point p ends
        /// up at p * HandRotation + Seat. Solving that for "the grip lands at the bone
        /// origin" gives -(grip * HandRotation): rotate into bone space first, THEN
        /// negate. Negating first looks plausible and is wrong the moment HandRotation
        /// stops being a half-turn.
        ///
        /// Zero for a model with no grip locator, which is correct for armor and the
        /// honest fallback for a weapon whose locator someone forgot.</summary>
        public Vector3 Seat(ItemType type) => Seat(type, HandRotationFor(type));

        public Vector3 Seat(ItemType type, Quaternion rotation) =>
            locators.Get(modelOf(type), "grip") is { } grip
                ? -Vector3.Transform(grip.Translation, rotation)
                : Vector3.Zero;

        public Vector3 FirstPersonModelOffset(ItemType type, Vector3 gripOffset, float scale = FirstPersonScale)
            => gripOffset + Seat(type, FirstPersonRotationFor(type)) * scale;

        public Vector3 FirstPersonMuzzleOffset(ItemType type, Vector3 gripOffset, float scale = FirstPersonScale)
        {
            var model = modelOf(type);
            if (locators.Get(model, "barrel") is not { } barrel)
                return FirstPersonModelOffset(type, gripOffset, scale);

            return FirstPersonModelOffset(type, gripOffset, scale)
                 + Vector3.Transform(barrel.Translation * scale, FirstPersonRotationFor(type));
        }

        /// <summary>Where this weapon's barrel is, relative to the player's origin and
        /// before the player's yaw is applied — i.e. the offset a shot starts at.
        ///
        /// Chains the same facts the renderer chains, in the same order: the barrel's offset from
        /// the grip in model space, rotated into hand space by HandRotation, placed by the hand's
        /// own transform in the firing pose, then swung about the aim bone by the aim pitch —
        /// because on screen that bone is what carries the arms and the gun. Get this pivot wrong
        /// and the shot leaves a point that drifts further from the drawn barrel the further you
        /// look up or down.
        ///
        /// Falls back to the player's centre height for a weapon with no barrel locator, which is
        /// wrong but visible rather than putting shots underground.</summary>
        public Vector3 Muzzle(ItemType type, float pitch = 0f)
        {
            var model = modelOf(type);
            if (locators.Get(model, "grip") is not { } grip || locators.Get(model, "barrel") is not { } barrel)
                return new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);

            var gripToBarrel = Vector3.Transform(barrel.Translation - grip.Translation, HandRotationFor(type));
            var unpitched = firingHand.Translation + Vector3.Transform(gripToBarrel, firingHand.Rotation);

            return aimPivot + Vector3.Transform(unpitched - aimPivot, PitchRotation(pitch));
        }
    }
}
