using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Demiurge
{
    /// <summary>
    /// A smoke puff's surface: spherical normals and a dithered fade. See
    /// assets/shaders/SmokeSurface.sdsl, which explains why the fade is a discard rather than a
    /// blend.
    ///
    /// It occupies the material's SURFACE slot — Stride's name for a feature allowed to rewrite the
    /// shading normal, and where a normal map would go. Registered as a final modifier at the PIXEL
    /// stage, so it lands after the geometry's own normal has been interpolated and overrides it.
    ///
    /// Opacity is a shader GENERIC rather than a bound parameter, for the reason the terrain's
    /// triplanar material uses generics: it is fixed per material, and as a literal the compiler
    /// folds the discard test into a constant comparison. That is what makes the caller's cache of
    /// a few fixed opacities the right shape — see SmokeManager.Tint.
    /// </summary>
    public sealed class MaterialSmokeSurfaceFeature : MaterialFeature, IMaterialSurfaceFeature
    {
        public MaterialSmokeSurfaceFeature(float alpha) => Alpha = alpha;

        public float Alpha { get; }

        public override void GenerateShader(MaterialGeneratorContext context)
            => context.SetStreamFinalModifier<MaterialSmokeSurfaceFeature>(
                MaterialShaderStage.Pixel,
                new ShaderClassSource("SmokeSurface", Alpha));
    }
}
