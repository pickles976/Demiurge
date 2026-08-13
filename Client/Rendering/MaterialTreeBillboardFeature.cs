using Stride.Rendering.Materials;
using Stride.Shaders;

namespace Demiurge
{
    /// <summary>
    /// Expands degenerate leaf quads into camera-facing cards in the vertex stage. See
    /// assets/shaders/TreeBillboard.sdsl for what the geometry has to look like going in.
    ///
    /// It occupies the material's DISPLACEMENT slot, which is Stride's name for "a feature that
    /// gets to move vertices"; <see cref="MaterialDisplacementMapFeature"/> is the engine's own
    /// implementation of the same slot and this mirrors how it registers.
    ///
    /// The size is a shader GENERIC rather than a bound parameter for the reason the terrain's
    /// triplanar material uses generics: it is fixed per material, and as a literal the compiler
    /// folds it into the offset instead of reading it from a cbuffer per vertex.
    /// </summary>
    public sealed class MaterialTreeBillboardFeature : MaterialFeature, IMaterialDisplacementFeature
    {
        public MaterialTreeBillboardFeature(float size)
        {
            Size = size;
        }

        /// <summary>Edge length of the card, in model units.</summary>
        public float Size { get; set; }

        public override void GenerateShader(MaterialGeneratorContext context)
            => context.SetStreamFinalModifier<MaterialTreeBillboardFeature>(
                MaterialShaderStage.Vertex,
                new ShaderClassSource("TreeBillboard", Size));

        public override bool Equals(object? obj)
            => obj is MaterialTreeBillboardFeature other && Size.Equals(other.Size);

        public override int GetHashCode() => Size.GetHashCode();
    }
}
