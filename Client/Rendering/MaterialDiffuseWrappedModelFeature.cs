using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Demiurge
{
    /// <summary>
    /// Diffuse model for thin foliage. A small amount of direct light wraps behind the geometric
    /// terminator, approximating transmission through a blade without flattening its directionality.
    /// </summary>
    public sealed class MaterialDiffuseWrappedModelFeature : MaterialFeature, IMaterialDiffuseModelFeature
    {
        public MaterialDiffuseWrappedModelFeature()
            : this(0.35f)
        {
        }

        public MaterialDiffuseWrappedModelFeature(float wrap)
        {
            Wrap = Math.Clamp(wrap, 0f, 1f);
        }

        public float Wrap { get; set; }

        public override void GenerateShader(MaterialGeneratorContext context)
        {
            context.AddShading(this).LightDependentSurface =
                new ShaderClassSource("MaterialSurfaceShadingDiffuseWrapped", Math.Clamp(Wrap, 0f, 1f));
        }

        public bool Equals(IMaterialShadingModelFeature? other)
            => other is MaterialDiffuseWrappedModelFeature wrapped && Wrap.Equals(wrapped.Wrap);

        public override bool Equals(object? obj)
            => obj is IMaterialShadingModelFeature feature && Equals(feature);

        public override int GetHashCode() => Wrap.GetHashCode();
    }
}
