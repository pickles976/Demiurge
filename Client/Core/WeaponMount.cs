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
        /// <summary>The rig every player wears; the hand a weapon hangs off is on it.</summary>
        public const string PlayerModel = "assets/models/cat_orange.gltf";

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
        /// Camera-relative grip position. The compact Glock needs extra eye relief in ADS;
        /// using this one entry point keeps its rendered model and muzzle origin together.
        /// </summary>
        public static Vector3 FirstPersonGripOffset(ItemType type, bool aiming)
            => aiming
                ? type == ItemType.Glock ? GlockAimGripOffset : AimGripOffset
                : HipGripOffset;

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
        public Vector3 Seat(ItemType type) => Seat(type, HandRotation);

        public Vector3 Seat(ItemType type, Quaternion rotation) =>
            locators.Get(modelOf(type), "grip") is { } grip
                ? -Vector3.Transform(grip.Translation, rotation)
                : Vector3.Zero;

        public Vector3 FirstPersonModelOffset(ItemType type, Vector3 gripOffset, float scale = FirstPersonScale)
            => gripOffset + Seat(type, FirstPersonRotation) * scale;

        public Vector3 FirstPersonMuzzleOffset(ItemType type, Vector3 gripOffset, float scale = FirstPersonScale)
        {
            var model = modelOf(type);
            if (locators.Get(model, "barrel") is not { } barrel)
                return FirstPersonModelOffset(type, gripOffset, scale);

            return FirstPersonModelOffset(type, gripOffset, scale)
                 + Vector3.Transform(barrel.Translation * scale, FirstPersonRotation);
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

            var gripToBarrel = Vector3.Transform(barrel.Translation - grip.Translation, HandRotation);
            var unpitched = firingHand.Translation + Vector3.Transform(gripToBarrel, firingHand.Rotation);

            return aimPivot + Vector3.Transform(unpitched - aimPivot, PitchRotation(pitch));
        }
    }
}
