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
    /// The tree view: the model's trunk and branches, plus <see cref="ScatterQuads"/>'s
    /// camera-facing leaf cards for the foliage.
    ///
    /// The model's own leaves_1..leaves_5 blobs are currently HIDDEN — <see cref="WithoutBlobs"/>
    /// drops them and <see cref="LeafMaterial"/> is parked — but they still shape what is drawn.
    /// The cards are scattered on shells around those blobs' centres and take their normals from
    /// them, which is the Airborn technique and the reason the canopy lights as one soft volume
    /// rather than as a pile of separately-lit cards.
    /// </summary>
    public static class TreeViewFactory
    {
        private const string ModelPath = "assets/models/tree.gltf";
        private const string LeafTexturePath = "assets/textures/leaf_alpha_texture.png";
        private const int LeafMaterialSlot = 1;
        private const int AnchorCount = 5;
        // A billboard always shows its whole area, where a fixed card mostly showed a foreshortened
        // sliver of it, so this wants fewer and smaller cards than the tangent version did.
        private const int ShellQuadsPerAnchor = 7;

        /// <summary>Cards filling each cluster's interior, standing in for the hidden blob's volume,
        /// and how far out of the shell's radius they reach.</summary>
        private const int InnerQuadsPerAnchor = 4;
        private const float InnerReach = 0.7f;

        private const int QuadsPerAnchor = ShellQuadsPerAnchor + InnerQuadsPerAnchor;

        /// <summary>How far light wraps past the terminator on a leaf card. At 0.6 a card edge-on to
        /// the sun still keeps 37% of full diffuse, where an unwrapped one would be black.</summary>
        private const float LeafWrap = 0.6f;

        /// <summary>What a card facing straight away from the sun keeps, by light coming through it.
        /// This is the floor on how dark any leaf can get.</summary>
        private const float LeafTranslucency = 0.25f;
        /// <summary>Shell the quads sit on, measured from the blob centre. The blobs are 3.5 x 3.5
        /// x 3.75, so this clears their faces by a little and sits well inside their corners.</summary>
        private const float SurfaceRadius = 2.4f;
        private const float SurfaceJitter = 0.15f;
        private const float QuadSize = 2.4f;

        /// <summary>How many mask tiles the blobs get. Their UVs span about 0.4, so this repeats the
        /// leaves roughly twice across a face.</summary>
        private const float BlobUvScale = 5f;

        /// <summary>The two ends the mask shades between: the leaves it draws are the darker green,
        /// and the surface they sit on is the lighter one.</summary>
        private static readonly Color4 LeafColor = new(0.15f, 0.32f, 0.13f, 1f);
        private static readonly Color4 BackingColor = new(0.31f, 0.60f, 0.25f, 1f);

        private static Model? trunkModel;
        private static Model? quadModel;
        private static Material? quadMaterial;
        private static Material? leafMaterial;
        private static Texture? leafTexture;

        public static Entity Create(Game game, ModelLocators locators)
        {
            var root = new Entity
            {
                new ModelComponent(WithoutBlobs(GLTFLoader.LoadModel(game, ModelPath))),
            };
            root.Transform.Children.Add(
                new Entity("TreeLeafCards")
                {
                    new ModelComponent(ScatterQuads(game, locators)),
                }.Transform);

            return root;
        }

        /// <summary>
        /// The tree with its leaf blobs left out: the same meshes and the same draw data, minus
        /// everything on the leaf material slot. Filtering the model rather than hiding the
        /// material means the blobs cost no draw call and no pixels, instead of being drawn and
        /// then thrown away.
        ///
        /// Built off the cached source model but never mutating it — Content.Load hands the same
        /// instance to every tree and to the editor preview.
        ///
        /// Note what goes with them: the blobs were also the OCCLUDER that hid cards on the far
        /// side of the canopy, so the cards now overdraw each other freely. <see cref="LeafMaterial"/>
        /// is parked rather than deleted for when they come back.
        /// </summary>
        private static Model WithoutBlobs(Model tree)
        {
            if (trunkModel != null) return trunkModel;

            var model = new Model
            {
                BoundingBox = tree.BoundingBox,
                BoundingSphere = tree.BoundingSphere,
                Skeleton = tree.Skeleton,
            };

            model.Add(tree.Materials[0]);
            foreach (var mesh in tree.Meshes)
            {
                if (mesh.MaterialIndex != LeafMaterialSlot) model.Add(mesh);
            }

            return trunkModel = model;
        }

        /// <summary>
        /// PARKED — nothing calls this while the blobs are hidden.
        ///
        /// Solid blobs under Stride's cel ramp, with the leaf mask tiled over them as colour
        /// variation — light and dark greens in leaf shapes, not holes. The geometry keeps its own
        /// silhouette; the texture only breaks up the flat interior.
        ///
        /// Opaque, so it needs neither a cutoff nor two-sided rendering: the blobs are closed boxes
        /// and their back faces are never seen.
        /// </summary>
        private static Material LeafMaterial(Game game)
            => leafMaterial ??= Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(
                        new ComputeTextureColor(LeafTexture(game))
                        {
                            // Scaled past the authored UV span so the mask tiles rather than
                            // stretching once over each face. Wrap is the sampler default, but it
                            // is the whole point here, so it says so.
                            Scale = new Vector2(BlobUvScale, BlobUvScale),
                            AddressModeU = TextureAddressMode.Wrap,
                            AddressModeV = TextureAddressMode.Wrap,
                        }),
                    DiffuseModel = new MaterialDiffuseCelShadingModelFeature(),
                },
            });

        /// <summary>
        /// Alpha-masked leaf cards scattered over each cluster, all of them in one shared mesh.
        /// Each card's four vertices are written at the SAME point — its place in the cluster, on
        /// the shell or inside it — and TreeBillboard.sdsl pulls them apart into a camera-facing
        /// quad in the vertex stage. A degenerate quad on the CPU is a billboard on the GPU, so
        /// this mesh is built once and never touched again however the camera moves.
        ///
        /// The normal is the outward direction from the cluster centre, not the card's own facing.
        /// That is the Airborn technique — normals lifted off an inner bubble, which here is the
        /// leaves_N geometry itself — and it is what makes the canopy light as one soft volume.
        ///
        /// The shell is centred on the node_N locator, which is authored at the centre of the
        /// matching leaves_N blob. It has to be a locator rather than the mesh node itself: only
        /// mesh-less nodes are extracted into the manifest, deliberately, so that a mesh and a
        /// locator may share a name.
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

                    // One direction does both jobs: where the card sits, and the normal it lights
                    // with. Interior cards keep the radial normal too — they are standing in for
                    // the blob's volume, so they should light as part of the same bubble.
                    var normal = OnSphere(quad, salt: 2);

                    // The first cards of each cluster line its surface; the rest fill the volume the
                    // hidden blob used to occupy, so the canopy is not a hollow shell seen through
                    // its own gaps. Cube-rooting spreads them evenly through that volume instead of
                    // piling them near the outside.
                    float radius = q < ShellQuadsPerAnchor
                        ? SurfaceRadius * RadiusJitter(quad)
                        : SurfaceRadius * InnerReach * MathF.Cbrt(Hash01(quad, 5));

                    var centre = origin + normal * radius;

                    // All four at the centre. The texture coordinate is the only thing telling the
                    // vertex shader which corner this is, so it has to span the full 0..1 square.
                    int v = quad * 4;
                    vertices[v + 0] = new VertexPositionNormalTexture(centre, normal, new Vector2(0f, 1f));
                    vertices[v + 1] = new VertexPositionNormalTexture(centre, normal, new Vector2(1f, 1f));
                    vertices[v + 2] = new VertexPositionNormalTexture(centre, normal, new Vector2(1f, 0f));
                    vertices[v + 3] = new VertexPositionNormalTexture(centre, normal, new Vector2(0f, 0f));

                    // TexCoord V runs down while the shader's offset runs up, so the winding that
                    // was front-facing for the tangent quads is reversed once expanded.
                    int ix = quad * 6;
                    indices[ix + 0] = v + 0;
                    indices[ix + 1] = v + 1;
                    indices[ix + 2] = v + 2;
                    indices[ix + 3] = v + 0;
                    indices[ix + 4] = v + 2;
                    indices[ix + 5] = v + 3;
                }
            }

            var vertexBuffer = Buffer.Vertex.New(game.GraphicsDevice, vertices, GraphicsResourceUsage.Default);
            var indexBuffer = Buffer.Index.New(game.GraphicsDevice, indices);
            // Every vertex is at a card centre, so the raw bounds describe the shell and nothing
            // else. The billboard grows each card by up to half its diagonal in whatever direction
            // the camera happens to be, and a box that does not allow for it culls the whole mesh
            // while its outermost leaves are still on screen.
            var bounds = BoundingBox.FromPoints(Array.ConvertAll(vertices, v => v.Position));
            float reach = QuadSize * 0.5f * MathF.Sqrt(2f);
            bounds = new BoundingBox(
                bounds.Minimum - new Vector3(reach),
                bounds.Maximum + new Vector3(reach));

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
                    // The card is built facing the camera but lit by the blob's outward normal, so
                    // its two sides are the same surface and either may be the one you see.
                    CullMode = CullMode.None,
                    Displacement = new MaterialTreeBillboardFeature(QuadSize),
                    Diffuse = new MaterialDiffuseMapFeature(
                        new ComputeTextureColor(LeafTexture(game))),
                    // A leaf is thin enough to pass light, so its shaded side is nowhere near as
                    // dark as a plain N.L makes it. Wrapping the terminator is the cheap stand-in
                    // for that transmission — the same feature the grass uses, but much further
                    // wrapped, because a canopy is layers of leaves lit through each other rather
                    // than single blades.
                    DiffuseModel = new MaterialDiffuseWrappedModelFeature(
                        wrap: LeafWrap, translucency: LeafTranslucency),
                    // Cutoff, not blend: the mask's edges are antialiased but its interior is hard,
                    // and alpha blending would need every card depth-sorted against every other.
                    Transparency = new MaterialTransparencyCutoffFeature
                    {
                        Alpha = new ComputeFloat(0.5f),
                    },
                },
            });

        /// <summary>
        /// The PNG is a greyscale shape mask with no colour of its own, so it gets read twice: its
        /// luminance shades the RGB from <see cref="BackingColor"/> where the mask is black to
        /// <see cref="LeafColor"/> where it is white, and the same luminance goes into the alpha.
        ///
        /// One texture then serves both users. The blobs are opaque and take only the colour, which
        /// is what turns the mask into leaf-shaped light and shade on a solid surface. The quads
        /// need the shape cut out and take the alpha as well.
        ///
        /// Decoded through StbImageSharp because Texture.Load pulls in Windows-only
        /// System.Drawing.Common.
        /// </summary>
        private static Texture LeafTexture(Game game)
        {
            if (leafTexture != null) return leafTexture;

            using var stream = File.OpenRead(LeafTexturePath);
            var mask = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            var pixels = new byte[mask.Width * mask.Height * 4];

            for (int i = 0; i < mask.Width * mask.Height; i++)
            {
                int p = i * 4;
                float t = mask.Data[p] / 255f;

                pixels[p + 0] = Channel(BackingColor.R, LeafColor.R, t);
                pixels[p + 1] = Channel(BackingColor.G, LeafColor.G, t);
                pixels[p + 2] = Channel(BackingColor.B, LeafColor.B, t);
                pixels[p + 3] = mask.Data[p];
            }

            return leafTexture = Texture.New2D(
                game.GraphicsDevice, mask.Width, mask.Height, PixelFormat.R8G8B8A8_UNorm_SRgb, pixels);

            static byte Channel(float dark, float light, float t)
                => (byte)(Math.Clamp(dark + (light - dark) * t, 0f, 1f) * 255f);
        }

        /// <summary>A little in or out of the shell, so the quads read as a rough surface rather
        /// than as a geometric sphere.</summary>
        private static float RadiusJitter(int index)
            => 1f + (Hash01(index, 4) * 2f - 1f) * SurfaceJitter;

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
