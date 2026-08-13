using Demiurge.GameClient;
using StbImageSharp;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Buffer = Stride.Graphics.Buffer;

namespace Demiurge
{
    /// <summary>
    /// The tree view. Foliage is the model's own leaves_1..leaves_5 geometry, which the glTF puts
    /// on its own untextured material — so it arrives as material slot 1 and gets a flat colour and
    /// cel shading here without touching the textured trunk in slot 0.
    ///
    /// The scattered quads that used to be the foliage are still below and currently unused; see
    /// <see cref="ScatterQuads"/>.
    /// </summary>
    public static class TreeViewFactory
    {
        private const string ModelPath = "assets/models/tree.gltf";
        private const string LeafTexturePath = "assets/textures/leaf_alpha_texture.png";
        private const int LeafMaterialSlot = 1;
        private const int AnchorCount = 5;
        private const int QuadsPerAnchor = 10;
        private const float ScatterRadius = 2f;
        private const float QuadSize = 3f;

        private static readonly Color4 LeafColor = new(0.23f, 0.47f, 0.19f, 1f);

        private static Model? quadModel;
        private static Material? quadMaterial;
        private static Material? leafMaterial;

        public static Entity Create(Game game, ModelLocators locators)
        {
            var model = new ModelComponent(GLTFLoader.LoadModel(game, ModelPath));

            // A per-component override, not an edit to the Model: Content.Load caches, so every
            // tree in the map shares that instance and mutating its materials would reach all of
            // them — and the editor's preview besides.
            model.Materials[LeafMaterialSlot] = LeafMaterial(game);

            return new Entity { model };
        }

        /// <summary>
        /// Flat colour under Stride's cel ramp: the leaves are a solid mass, and the whole point of
        /// the banding is that it reads as shape without any texture on it.
        /// </summary>
        private static Material LeafMaterial(Game game)
            => leafMaterial ??= Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(LeafColor)),
                    DiffuseModel = new MaterialDiffuseCelShadingModelFeature(),
                },
            });

        /// <summary>
        /// PARKED. Ten alpha-masked quads scattered around each of the five anchor locators, all
        /// fifty in one shared mesh. It was the foliage before the model carried its own, and is
        /// kept because the solid geometry may yet want quads on top of it. To put it back, hang
        /// the returned model on a child entity of the root in <see cref="Create"/>.
        /// </summary>
        private static Model ScatterQuads(Game game, ModelLocators locators)
        {
            if (quadModel != null) return quadModel;

            const int quads = AnchorCount * QuadsPerAnchor;
            var vertices = new VertexPositionNormalTexture[quads * 4];
            var indices = new int[quads * 6];

            for (int anchor = 0; anchor < AnchorCount; anchor++)
            {
                var origin = locators.Require(ModelPath, $"node_{anchor + 1}").Translation.ToStride();

                for (int q = 0; q < QuadsPerAnchor; q++)
                {
                    int quad = anchor * QuadsPerAnchor + q;
                    var centre = origin + InSphere(quad) * ScatterRadius;

                    // Each quad faces its own way. A camera-facing billboard needs per-frame work
                    // or a shader; a fixed random orientation reads as a foliage cluster and needs
                    // neither.
                    var normal = OnSphere(quad, salt: 3);
                    var right = Vector3.Cross(Vector3.UnitY, normal);
                    if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
                    right.Normalize();
                    var up = Vector3.Cross(normal, right);

                    float half = QuadSize * 0.5f;
                    right *= half;
                    up *= half;

                    int v = quad * 4;
                    vertices[v + 0] = new VertexPositionNormalTexture(centre - right - up, normal, new Vector2(0f, 1f));
                    vertices[v + 1] = new VertexPositionNormalTexture(centre + right - up, normal, new Vector2(1f, 1f));
                    vertices[v + 2] = new VertexPositionNormalTexture(centre + right + up, normal, new Vector2(1f, 0f));
                    vertices[v + 3] = new VertexPositionNormalTexture(centre - right + up, normal, new Vector2(0f, 0f));

                    int ix = quad * 6;
                    indices[ix + 0] = v + 0;
                    indices[ix + 1] = v + 2;
                    indices[ix + 2] = v + 1;
                    indices[ix + 3] = v + 0;
                    indices[ix + 4] = v + 3;
                    indices[ix + 5] = v + 2;
                }
            }

            var vertexBuffer = Buffer.Vertex.New(game.GraphicsDevice, vertices, GraphicsResourceUsage.Default);
            var indexBuffer = Buffer.Index.New(game.GraphicsDevice, indices);
            var bounds = BoundingBox.FromPoints(Array.ConvertAll(vertices, v => v.Position));

            quadModel = new Model();
            quadModel.Add(new Mesh
            {
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = indices.Length,
                    IndexBuffer = new IndexBufferBinding(indexBuffer, is32Bit: true, indices.Length),
                    VertexBuffers =
                    [
                        new VertexBufferBinding(vertexBuffer, VertexPositionNormalTexture.Layout, vertices.Length)
                    ],
                },
                MaterialIndex = 0,
                BoundingBox = bounds,
                BoundingSphere = BoundingSphere.FromBox(bounds),
            });
            quadModel.Add(new MaterialInstance(QuadMaterial(game)));

            return quadModel;
        }

        private static Material QuadMaterial(Game game)
            => quadMaterial ??= Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    // A quad is one-sided geometry and its orientation is random, so half of them
                    // would otherwise be invisible from any given side.
                    CullMode = CullMode.None,
                    Diffuse = new MaterialDiffuseMapFeature(
                        new ComputeTextureColor(QuadTexture(game))),
                    // Wrapped rather than Lambert, for the same reason the grass uses it: with the
                    // quads pointing every way, the ones facing away from the sun go black under a
                    // plain N.L and the canopy reads as holes.
                    DiffuseModel = new MaterialDiffuseWrappedModelFeature(wrap: 0.3f),
                    // Cutoff, not blend: the mask's edges are antialiased but its interior is hard,
                    // and alpha blending would need these fifty quads depth-sorted against each other.
                    Transparency = new MaterialTransparencyCutoffFeature
                    {
                        Alpha = new ComputeFloat(0.5f),
                    },
                },
            });

        /// <summary>
        /// The PNG is a greyscale shape mask with no alpha channel of its own, so its luminance
        /// becomes the alpha and <see cref="LeafColor"/> fills in the RGB. Decoded through
        /// StbImageSharp because Texture.Load pulls in Windows-only System.Drawing.Common.
        /// </summary>
        private static Texture QuadTexture(Game game)
        {
            using var stream = File.OpenRead(LeafTexturePath);
            var mask = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            var pixels = new byte[mask.Width * mask.Height * 4];
            byte r = (byte)(LeafColor.R * 255f);
            byte g = (byte)(LeafColor.G * 255f);
            byte b = (byte)(LeafColor.B * 255f);

            for (int i = 0; i < mask.Width * mask.Height; i++)
            {
                int p = i * 4;
                pixels[p + 0] = r;
                pixels[p + 1] = g;
                pixels[p + 2] = b;
                pixels[p + 3] = mask.Data[p];
            }

            return Texture.New2D(
                game.GraphicsDevice, mask.Width, mask.Height, PixelFormat.R8G8B8A8_UNorm_SRgb, pixels);
        }

        /// <summary>A point inside the unit sphere. Cube-rooting the radius keeps the scatter
        /// even instead of piling every quad up against the outside.</summary>
        private static Vector3 InSphere(int index)
            => OnSphere(index, salt: 2) * MathF.Cbrt(Hash01(index, 4));

        private static Vector3 OnSphere(int index, int salt)
        {
            float y = Hash01(index, salt) * 2f - 1f;
            float phi = Hash01(index, salt + 8) * MathUtil.TwoPi;
            float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            return new Vector3(r * MathF.Cos(phi), y, r * MathF.Sin(phi));
        }

        private static float Hash01(int x, int salt)
        {
            unchecked
            {
                uint h = (uint)x * 0x8DA6B343u ^ (uint)salt * 0xCB1AB31Fu;
                h ^= h >> 13;
                h *= 0x85EBCA6Bu;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
