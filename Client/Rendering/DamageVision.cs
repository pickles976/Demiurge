using Stride.Rendering;
using Stride.Rendering.Images;

namespace Demiurge
{
    /// <summary>
    /// The colour transform that shows the player how badly hurt they are: a red wash that spikes
    /// when a wound lands and fades, and a desaturation that comes on below
    /// <see cref="HealthConfig.DesaturationFraction"/> and deepens toward death.
    ///
    /// A post-process rather than a HUD overlay because desaturation has to READ the frame — no
    /// rectangle drawn on top can drain colour out of what is behind it. Keeping the red here as
    /// well means the two are one effect with one ordering, instead of a shader and a UI element
    /// that have to be kept in step by hand.
    ///
    /// The numbers come from <see cref="HealthConfig"/>, shared with the server, so the picture and
    /// the health bar cannot disagree about what "nearly dead" means.
    /// </summary>
    public sealed class DamageVision : ColorTransform
    {
        /// <summary>How fast a hit's red spike fades. Fast: it is a flinch, not a status effect —
        /// the sustained signal is the desaturation.</summary>
        private const float FlashDecayPerSecond = 1.9f;

        /// <summary>Red from a fresh wound, on top of whatever the health level already contributes.</summary>
        private const float FlashPerHealthFraction = 2.2f;

        /// <summary>Ceiling on the sustained red so a nearly-dead player can still see the game.</summary>
        private const float MaximumSustainedWash = 0.55f;

        // Named rather than generated: this project has no .sdsl.cs key generation (see
        // TerrainMaterials and LineRenderer, which do the same), so the names must match the
        // shader's members exactly.
        //
        // And they are resolved from the REGISTRY, not simply constructed. ColorTransformGroup folds
        // every transform into one shader and copies each transform's Parameters across through a
        // CompositionCopier keyed on the parameter objects the compiled effect registered. A key
        // built locally with ParameterKeys.NewValue can be a second object with the same name, which
        // the copier then never looks for — values set, shader running, nothing on screen. Asking
        // the registry for the name gets the instance the effect actually uses.
        private static readonly ValueParameterKey<float> LocalDesaturationKey =
            ParameterKeys.NewValue(0f, "DamageVisionShader.Desaturation");
        private static readonly ValueParameterKey<float> LocalRedWashKey =
            ParameterKeys.NewValue(0f, "DamageVisionShader.RedWash");

        private ValueParameterKey<float>? desaturationKey;
        private ValueParameterKey<float>? redWashKey;

        /// <summary>
        /// The effect's own key for a name, or the locally built one until the effect exists. Cached
        /// after the first successful lookup — the registry is populated by shader compilation, so
        /// the first frame or two can legitimately miss.
        /// </summary>
        private static ValueParameterKey<float> Resolve(
            ref ValueParameterKey<float>? cached,
            ValueParameterKey<float> local)
        {
            if (cached is not null) return cached;
            if (ParameterKeys.FindByName(local.Name) is ValueParameterKey<float> registered)
                cached = registered;
            return cached ?? local;
        }

        private float flash;
        private float desaturation;
        private float wash;

        public DamageVision()
            : base("DamageVisionShader")
        {
        }

        /// <summary>
        /// Folds this frame's health into the effect. <paramref name="lostFraction"/> is health lost
        /// since the last call, as a fraction of maximum — the spike — while
        /// <paramref name="healthFraction"/> drives the steady state.
        /// </summary>
        public void Update(float healthFraction, float lostFraction, float dt, bool alive)
        {
            if (!float.IsFinite(dt) || dt < 0f) dt = 0f;

            if (!alive)
            {
                // Dead is the killcam's business, not this effect's. Left on, it would tint the
                // whole spectate view of a corpse that no longer has health to speak of.
                flash = desaturation = wash = 0f;
                return;
            }

            flash = MathF.Max(0f, flash - FlashDecayPerSecond * dt);
            if (lostFraction > 0f)
                flash = MathF.Min(1f, flash + lostFraction * FlashPerHealthFraction);

            // Below the threshold, and harder the closer to zero. Squared so the last sliver of
            // health is unmistakably different from merely being hurt.
            float drained = healthFraction >= HealthConfig.DesaturationFraction
                ? 0f
                : 1f - healthFraction / HealthConfig.DesaturationFraction;
            desaturation = drained * drained;

            float sustained = (1f - healthFraction) * MaximumSustainedWash;
            wash = MathF.Min(1f, MathF.Max(sustained, flash));
        }

        public override void UpdateParameters(ColorTransformContext context)
        {
            base.UpdateParameters(context);
            Parameters.Set(Resolve(ref desaturationKey, LocalDesaturationKey), desaturation);
            Parameters.Set(Resolve(ref redWashKey, LocalRedWashKey), wash);

        }
    }
}
