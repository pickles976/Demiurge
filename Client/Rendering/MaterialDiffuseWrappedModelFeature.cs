using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Demiurge
{
    /// <summary>
    /// Diffuse model for thin foliage. A small amount of direct light wraps behind the geometric
    /// terminator, approximating transmission through a blade without flattening its directionality.
    ///
    /// <see cref="Translucency"/> is the other half of that transmission and covers what wrapping
    /// cannot: a surface facing directly away from the light is still exactly black however far the
    /// terminator is wrapped, and a leaf is not. It defaults to zero, so a caller that only wants
    /// the wrap gets what it always got.
    /// </summary>
    public sealed class MaterialDiffuseWrappedModelFeature : MaterialFeature, IMaterialDiffuseModelFeature
    {
        public MaterialDiffuseWrappedModelFeature()
            : this(0.35f)
        {
        }

        public MaterialDiffuseWrappedModelFeature(float wrap, float translucency = 0f)
        {
            Wrap = Math.Clamp(wrap, 0f, 1f);
            Translucency = Math.Clamp(translucency, 0f, 1f);
        }

        public float Wrap { get; set; }

        /// <summary>Fraction of the light that reaches a surface turned fully away from it.</summary>
        public float Translucency { get; set; }

        public override void GenerateShader(MaterialGeneratorContext context)
        {
            context.AddShading(this).LightDependentSurface =
                new ShaderClassSource(
                    "MaterialSurfaceShadingDiffuseWrapped",
                    Math.Clamp(Wrap, 0f, 1f),
                    Math.Clamp(Translucency, 0f, 1f));
        }

        // Stride keys shading models on this, so both generics have to take part: two materials
        // differing only in translucency would otherwise be handed the same compiled shader.
        public bool Equals(IMaterialShadingModelFeature? other)
            => other is MaterialDiffuseWrappedModelFeature wrapped
               && Wrap.Equals(wrapped.Wrap)
               && Translucency.Equals(wrapped.Translucency);

        public override bool Equals(object? obj)
            => obj is IMaterialShadingModelFeature feature && Equals(feature);

        public override int GetHashCode() => HashCode.Combine(Wrap, Translucency);
    }
}
