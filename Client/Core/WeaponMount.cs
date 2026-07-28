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

        /// <summary>The pose the muzzle is measured in. Firing is aiming-only
        /// (LocalPlayerController gates TryFire on it), and this clip holds the arm still
        /// — its hand transform is constant to four decimals across the whole clip — so
        /// one baked pose is exact here rather than an average of a moving arm.</summary>
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

        private readonly ModelLocators locators;
        private readonly Func<ItemType, string> modelOf;
        private readonly ModelLocators.Pose firingHand;

        public WeaponMount(ModelLocators locators, Func<ItemType, string> modelOf)
        {
            this.locators = locators;
            this.modelOf = modelOf;
            firingHand = locators.Require(PlayerModel, HandBone, FiringPose);
        }

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
        public Vector3 Seat(ItemType type) =>
            locators.Get(modelOf(type), "grip") is { } grip
                ? -Vector3.Transform(grip.Translation, HandRotation)
                : Vector3.Zero;

        /// <summary>Where this weapon's barrel is, relative to the player's origin and
        /// before the player's yaw is applied — i.e. the offset a shot starts at.
        ///
        /// Chains the same three facts the renderer chains: the barrel's offset from the
        /// grip in model space, rotated into hand space by HandRotation, then placed by
        /// the hand's own transform in the firing pose. Falls back to the player's centre
        /// height for a weapon with no barrel locator, which is wrong but visible rather
        /// than putting shots underground.</summary>
        public Vector3 Muzzle(ItemType type)
        {
            var model = modelOf(type);
            if (locators.Get(model, "grip") is not { } grip || locators.Get(model, "barrel") is not { } barrel)
                return new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);

            var gripToBarrel = Vector3.Transform(barrel.Translation - grip.Translation, HandRotation);
            return firingHand.Translation + Vector3.Transform(gripToBarrel, firingHand.Rotation);
        }
    }
}
