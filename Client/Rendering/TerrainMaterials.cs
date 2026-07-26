using StbImageSharp;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Demiurge
{
    /// <summary>
    /// One Stride material per <see cref="BlockType"/>, built from <see cref="BlockTextures"/>.
    ///
    /// Each material is triplanar — the mesh has no usable UVs, since a dual mesh puts vertices at
    /// arbitrary points on an isosurface — and carries its type's variants as a Texture2DArray, with
    /// the shader picking a slice per world cell.
    ///
    /// Texture2DArray rather than an atlas: atlas tiles bleed into each other under filtering and
    /// mipmapping, which needs padding and gets fiddly precisely because triplanar fabricates its own
    /// UVs. Array slices are independent, so a new variant is a file drop.
    /// </summary>
    public sealed class TerrainMaterials
    {
        readonly Dictionary<BlockType, Material> materials = new();

        public TerrainMaterials(Game game)
        {
            foreach (BlockType type in Enum.GetValues<BlockType>())
            {
                if (type == BlockType.BlockType_Air) continue;   // air is never drawn

                // Every non-air type gets a material, so For() can't miss: a type with no art in the
                // manifest builds from BlockTextures.Missing and draws obviously-placeholder purple.
                materials[type] = Build(game, BlockTextures.For(type));
            }
        }

        public Material For(BlockType type) => materials[type];

        static Material Build(Game game, BlockTextures.Entry entry)
        {
            var textures = LoadArray(game, entry);

            // The variant count and tile size are shader GENERICS, not bound parameters: they're
            // fixed per material, and as literals the compiler can specialise away the variant hash
            // for a single-variant type.
            var triplanar = new ComputeShaderClassColor { MixinReference = "TriplanarTexture" };
            triplanar.Generics.Add("TVariants", new ComputeColorParameterFloat { Value = entry.Variants });
            triplanar.Generics.Add("TTileSize", new ComputeColorParameterFloat { Value = entry.TileSize });

            var material = Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes = new MaterialAttributes
                {
                    Diffuse = new MaterialDiffuseMapFeature(triplanar),
                    DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                }
            });

            // A Texture2DArray can't arrive through a ComputeTextureColor — that only produces
            // Texture2D — so it binds by name against the shader's rgroup member instead.
            material.Passes[0].Parameters.Set(VariantTexturesKey, textures);

            return material;
        }

        static readonly ObjectParameterKey<Texture> VariantTexturesKey =
            ParameterKeys.NewObject<Texture>(null, "TriplanarTexture.VariantTextures");

        /// <summary>
        /// Loads every variant PNG into one Texture2DArray. All variants must share dimensions, which
        /// is a hardware requirement for array textures, so a mismatch throws here rather than
        /// rendering something inexplicable.
        /// </summary>
        static Texture LoadArray(Game game, BlockTextures.Entry entry)
        {
            var images = new ImageResult[entry.Variants];

            for (int variant = 0; variant < entry.Variants; variant++)
            {
                // Texture.Load pulls in Windows-only System.Drawing.Common; decode via StbImageSharp.
                using var stream = File.OpenRead(entry.Paths[variant]);
                images[variant] = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

                if (images[variant].Width != images[0].Width || images[variant].Height != images[0].Height)
                {
                    throw new InvalidOperationException(
                        $"{entry.Paths[variant]} is {images[variant].Width}x{images[variant].Height}, but " +
                        $"{entry.Paths[0]} is {images[0].Width}x{images[0].Height}. " +
                        "All variants of one block type must match — they share a Texture2DArray.");
                }
            }

            var texture = Texture.New2D(game.GraphicsDevice, images[0].Width, images[0].Height,
                PixelFormat.R8G8B8A8_UNorm_SRgb, TextureFlags.ShaderResource,
                arraySize: entry.Variants);

            for (int variant = 0; variant < entry.Variants; variant++)
                texture.SetData(game.GraphicsContext.CommandList, images[variant].Data, arraySlice: variant);

            return texture;
        }
    }
}
