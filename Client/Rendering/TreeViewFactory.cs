using Demiurge.GameClient;
using NoiseDotNet;
using StbImageSharp;
using System.Runtime.InteropServices;
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
        private const float InnerReach = 0.7f;

        /// <summary>
        /// How much canopy a tree gets. Near, a billboard shows its whole area where a fixed card
        /// showed a foreshortened sliver, so it wants many small ones; far, all that survives is the
        /// silhouette, and a handful of big cards draw the same shape for a sixth of the work.
        ///
        /// The cards do NOT line up between the two — the scatter is keyed on the card's index, and
        /// there are different numbers of them — so the swap is a change of shape, not a change of
        /// resolution. Keeping both on the same shell radius is what stops that reading as a jump.
        /// </summary>
        public enum LeafDetail { Near, Far }

        private readonly record struct CardDetail(
            int ShellPerAnchor, int InnerPerAnchor, float Size)
        {
            public int PerAnchor => ShellPerAnchor + InnerPerAnchor;
        }

        private static readonly CardDetail NearCards = new(14, 8, 2.4f);
        private static readonly CardDetail FarCards = new(3, 0, 5.2f);

        private static CardDetail Cards(LeafDetail detail)
            => detail == LeafDetail.Near ? NearCards : FarCards;
        /// <summary>Reach of each cluster's contribution to the canopy density that drives per-card
        /// occlusion. It has to be wide enough that neighbouring clusters overlap near the trunk —
        /// that overlap IS the middle of the tree.</summary>
        private const float CanopyRadius = 5f;

        /// <summary>How dark the most buried card gets, and how much darker the underside of the
        /// canopy is than its crown.</summary>
        private const float CardAoDepth = 0.45f;
        private const float CardAoUnderside = 0.25f;

        /// <summary>Scale of the patchiness across the canopy, and how far it swings the brightness
        /// and the colour. The wavelength wants to be a fraction of the canopy — much larger and the
        /// whole tree shifts together, much smaller and it turns back into per-card static.</summary>
        private const float FoliageNoiseWavelength = 3.5f;
        private const float FoliageValueNoise = 0.18f;
        private const float FoliageHueNoise = 0.10f;

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

        /// <summary>The unit square in cyclic order, so stepping through it turns a card.</summary>
        private static readonly Vector2[] CardCorners =
            [new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)];

        /// <summary>Where the mask's soft edge is made hard. The material cuts at the same value, so
        /// the texture and the test agree on exactly which texels are leaf.</summary>
        private const float AlphaCutoff = 0.5f;

        /// <summary>How many mask tiles the blobs get. Their UVs span about 0.4, so this repeats the
        /// leaves roughly twice across a face.</summary>
        private const float BlobUvScale = 5f;

        /// <summary>How dark a texel gets when it is completely enclosed by leaf, and how far out
        /// "enclosed" is measured — the blur reaching further makes the occlusion broader and
        /// softer, less a shadow around each leaf and more a gradient across a clump.</summary>
        private const float AoStrength = 0.32f;
        private const int AoBlurRadius = 10;
        private const int AoBlurPasses = 3;

        /// <summary>The colour of a leaf under full light, before occlusion and shading. It is the
        /// only colour in the texture now — see <see cref="LeafTexture"/> on why the transparent
        /// region cannot hold a different one.</summary>
        private static readonly Color4 LeafColor = new(0.225f, 0.48f, 0.195f, 1f);

        private static readonly Dictionary<LeafDetail, Model> woodyModels = [];
        private static readonly Dictionary<LeafDetail, Model> quadModels = [];
        private static readonly Dictionary<LeafDetail, Material> quadMaterials = [];
        private static Material? leafMaterial;
        private static Texture? leafTexture;

        public static Entity Create(Game game, ModelLocators locators, LeafDetail detail)
        {
            var root = new Entity { new ModelComponent(Woody(game, detail)) };
            root.Transform.Children.Add(
                new Entity(LeafEntityName)
                {
                    new ModelComponent(ScatterQuads(game, locators, detail)),
                }.Transform);

            return root;
        }

        /// <summary>
        /// Moves an existing tree between detail levels by swapping the two models it draws. The
        /// entity, its transform and its place in the scene are untouched, which is the point: a
        /// tree never stops being drawn, it only stops being drawn in detail.
        /// </summary>
        public static void SetDetail(Entity tree, Game game, ModelLocators locators, LeafDetail detail)
        {
            if (tree.Get<ModelComponent>() is { } woody) woody.Model = Woody(game, detail);

            foreach (var child in tree.Transform.Children)
            {
                if (child.Entity.Name == LeafEntityName
                    && child.Entity.Get<ModelComponent>() is { } leaves)
                    leaves.Model = ScatterQuads(game, locators, detail);
            }
        }

        private const string LeafEntityName = "TreeLeafCards";

        /// <summary>
        /// The solid parts: trunk and branches near, trunk alone far.
        ///
        /// The blobs are dropped from both. Filtering the model rather than hiding the material
        /// means they cost no draw call and no pixels, instead of being drawn and then thrown away —
        /// and note what went with them, since the blob was also the OCCLUDER that hid cards on the
        /// far side of the canopy. <see cref="LeafMaterial"/> is parked for if they come back.
        ///
        /// Far, the four branches go too. They are a metre or two of geometry seen at a hundred, and
        /// they are four of the six draw calls a tree costs. The trunk is found by height rather
        /// than by mesh order, so re-exporting the model with the parts in a different order — or
        /// with more branches — does not quietly promote a branch to being the trunk.
        ///
        /// Built off the cached source model but never mutating it: Content.Load hands the same
        /// instance to every tree and to the editor preview.
        /// </summary>
        private static Model Woody(Game game, LeafDetail detail)
        {
            if (woodyModels.TryGetValue(detail, out var cached)) return cached;

            var tree = GLTFLoader.LoadModel(game, ModelPath);
            var model = new Model
            {
                BoundingBox = tree.BoundingBox,
                BoundingSphere = tree.BoundingSphere,
                Skeleton = tree.Skeleton,
            };
            model.Add(tree.Materials[0]);

            var solid = tree.Meshes.Where(mesh => mesh.MaterialIndex != LeafMaterialSlot);
            if (detail == LeafDetail.Far)
            {
                var trunk = solid
                    .OrderByDescending(mesh => mesh.BoundingBox.Maximum.Y - mesh.BoundingBox.Minimum.Y)
                    .FirstOrDefault();
                solid = trunk is null ? [] : [trunk];
            }

            foreach (var mesh in solid) model.Add(mesh);

            return woodyModels[detail] = model;
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
        private static Model ScatterQuads(Game game, ModelLocators locators, LeafDetail detail)
        {
            if (quadModels.TryGetValue(detail, out var cached)) return cached;

            var cards = Cards(detail);
            int quads = AnchorCount * cards.PerAnchor;

            var clusters = new Vector3[AnchorCount];
            for (int anchor = 0; anchor < AnchorCount; anchor++)
                clusters[anchor] = locators.Require(ModelPath, $"node_{anchor + 1}").Translation.ToStride();

            var centres = new Vector3[quads];
            var normals = new Vector3[quads];

            for (int anchor = 0; anchor < AnchorCount; anchor++)
            {
                for (int q = 0; q < cards.PerAnchor; q++)
                {
                    int quad = anchor * cards.PerAnchor + q;

                    // One direction does both jobs: where the card sits, and the normal it lights
                    // with. Interior cards keep the radial normal too — they are standing in for
                    // the blob's volume, so they should light as part of the same bubble.
                    normals[quad] = OnSphere(quad, salt: 2);

                    // The first cards of each cluster line its surface; the rest fill the volume the
                    // hidden blob used to occupy, so the canopy is not a hollow shell seen through
                    // its own gaps. Cube-rooting spreads them evenly through that volume instead of
                    // piling them near the outside.
                    float radius = q < cards.ShellPerAnchor
                        ? SurfaceRadius * RadiusJitter(quad)
                        : SurfaceRadius * InnerReach * MathF.Cbrt(Hash01(quad, 5));

                    centres[quad] = clusters[anchor] + normals[quad] * radius;
                }
            }

            var occlusion = BakeOcclusion(centres, clusters);
            var variation = BakeVariation(centres);
            var vertices = new LeafCardVertex[quads * 4];
            var indices = new int[quads * 6];

            for (int quad = 0; quad < quads; quad++)
            {
                // All four vertices sit at the centre; the corner channel is what the vertex shader
                // pulls them apart by. Turning those corners spins the card in the plane it faces —
                // and since the texture coordinate stays put on the unit square, the image turns
                // with the card and never samples past the texture's edge.
                float angle = Hash01(quad, 6) * MathF.Tau;
                float sin = MathF.Sin(angle);
                float cos = MathF.Cos(angle);
                var shade = Shade(occlusion[quad], variation[quad]);

                int v = quad * 4;
                for (int corner = 0; corner < 4; corner++)
                {
                    var uv = CardCorners[corner];
                    float x = uv.X - 0.5f;
                    float y = uv.Y - 0.5f;

                    vertices[v + corner] = new LeafCardVertex(
                        centres[quad],
                        normals[quad],
                        shade,
                        uv,
                        new Vector2(x * cos - y * sin, x * sin + y * cos));
                }

                int ix = quad * 6;
                indices[ix + 0] = v + 0;
                indices[ix + 1] = v + 1;
                indices[ix + 2] = v + 2;
                indices[ix + 3] = v + 0;
                indices[ix + 4] = v + 2;
                indices[ix + 5] = v + 3;
            }

            var vertexBuffer = Buffer.Vertex.New(game.GraphicsDevice, vertices, GraphicsResourceUsage.Default);
            var indexBuffer = Buffer.Index.New(game.GraphicsDevice, indices);

            // Every vertex is at a card centre, so the raw bounds describe the shell and nothing
            // else. The billboard grows each card by up to half its diagonal in whatever direction
            // the camera happens to be, and a box that does not allow for it culls the whole mesh
            // while its outermost leaves are still on screen.
            var bounds = BoundingBox.FromPoints(Array.ConvertAll(vertices, v => v.Position));
            float reach = cards.Size * 0.5f * MathF.Sqrt(2f);
            bounds = new BoundingBox(
                bounds.Minimum - new Vector3(reach),
                bounds.Maximum + new Vector3(reach));

            var model = new Model();
            model.Add(new Mesh
            {
                Draw = new MeshDraw
                {
                    PrimitiveType = PrimitiveType.TriangleList,
                    DrawCount = indices.Length,
                    IndexBuffer = new IndexBufferBinding(indexBuffer, is32Bit: true, indices.Length),
                    VertexBuffers =
                    [
                        new VertexBufferBinding(vertexBuffer, LeafCardVertex.Layout, vertices.Length)
                    ],
                },
                MaterialIndex = 0,
                BoundingBox = bounds,
                BoundingSphere = BoundingSphere.FromBox(bounds),
            });
            model.Add(new MaterialInstance(QuadMaterial(game, detail)));

            quadModels[detail] = model;
            return model;
        }

        /// <summary>
        /// Per-card colour variation, sampled from gradient noise at the card's own position rather
        /// than hashed from its index. That is the whole point: noise is CONTINUOUS, so neighbouring
        /// cards land on similar values and the canopy breaks into patches of lighter and darker
        /// foliage. A per-card hash would be white noise and read as static — every card different
        /// from its neighbour, which is uniform in its own way.
        ///
        /// Two-dimensional noise folded over height, because the only generator in the codebase is
        /// 2D and a canopy is wide rather than tall; sampling it in a plane through the tree gives
        /// enough variety without pretending to a 3D field we do not have.
        /// </summary>
        private static float[] BakeVariation(Vector3[] centres)
        {
            var xs = new float[centres.Length];
            var ys = new float[centres.Length];

            for (int i = 0; i < centres.Length; i++)
            {
                xs[i] = centres[i].X + centres[i].Y * 0.7f;
                ys[i] = centres[i].Z + centres[i].Y * 0.4f;
            }

            var noise = new float[centres.Length];
            Noise.GradientNoise2D(xs, ys, noise, new NoiseSettings
            {
                XFrequency = 1f / FoliageNoiseWavelength,
                YFrequency = 1f / FoliageNoiseWavelength,
                Amplitude = 1f,
                Seed = NoiseGen.Seed + 900,
            });

            // The generator's practical range, the same normalisation the terrain and the tree
            // placement apply to it.
            for (int i = 0; i < noise.Length; i++)
                noise[i] = Math.Clamp(noise[i] / 0.70f, -1f, 1f);

            return noise;
        }

        /// <summary>
        /// One card's entry in the colour stream: its occlusion, modulated by the foliage noise in
        /// both value and hue. The hue term pushes opposite ways in red and blue, which turns a
        /// single noise value into a yellow-green to blue-green axis — the way real foliage varies —
        /// rather than just making patches lighter and darker.
        /// </summary>
        private static Color Shade(float occlusion, float noise)
        {
            float value = occlusion * (1f + noise * FoliageValueNoise);
            float hue = noise * FoliageHueNoise;

            return new Color(
                Math.Clamp(value * (1f + hue), 0f, 1f),
                Math.Clamp(value, 0f, 1f),
                Math.Clamp(value * (1f - hue), 0f, 1f),
                1f);
        }

        /// <summary>
        /// Per-card ambient occlusion, baked once into the mesh. Neither reference technique does
        /// this — Airborn's bubble gives soft NORMALS and then hides the inner cards by being opaque,
        /// and the Godot shader has no occlusion at all — but with the blobs hidden, nothing is
        /// hiding the inner cards, so the darkening has to be shaded rather than culled.
        ///
        /// The measure is how much canopy surrounds a card, as a sum of soft falloffs from every
        /// cluster centre. That gets "closer to the middle of the tree is darker" for free and for
        /// the right reason: the middle is where several clusters' influence overlaps. Measuring
        /// depth within each cluster SEPARATELY, which is what the two-band version did, cannot see
        /// this at all — a card on the trunk side of a cluster is on that cluster's surface and
        /// buried in the tree, and those are the ones that were wrongly bright.
        ///
        /// The range is normalised against the cards actually built rather than against a tuned
        /// constant, so re-exporting the model with clusters somewhere else re-fits by itself.
        /// </summary>
        private static float[] BakeOcclusion(Vector3[] centres, Vector3[] clusters)
        {
            var density = new float[centres.Length];
            float lowest = float.MaxValue;
            float highest = float.MinValue;

            for (int i = 0; i < centres.Length; i++)
            {
                foreach (var cluster in clusters)
                {
                    float distance = (centres[i] - cluster).Length();
                    density[i] += Math.Max(0f, 1f - distance / CanopyRadius);
                }

                lowest = Math.Min(lowest, density[i]);
                highest = Math.Max(highest, density[i]);
            }

            // Height within the canopy, so the underside is darker than the crown. A leaf's sky is
            // mostly straight up, and no amount of surrounding-density says which way is up.
            float floor = float.MaxValue;
            float ceiling = float.MinValue;
            foreach (var centre in centres)
            {
                floor = Math.Min(floor, centre.Y);
                ceiling = Math.Max(ceiling, centre.Y);
            }

            var occlusion = new float[centres.Length];
            float spread = Math.Max(1e-4f, highest - lowest);
            float rise = Math.Max(1e-4f, ceiling - floor);

            for (int i = 0; i < centres.Length; i++)
            {
                float buried = (density[i] - lowest) / spread;
                float sky = (centres[i].Y - floor) / rise;

                occlusion[i] = Math.Clamp(
                    (1f - CardAoDepth * buried) * (1f - CardAoUnderside * (1f - sky)), 0f, 1f);
            }

            return occlusion;
        }

        /// <summary>
        /// Position, the bubble normal, baked occlusion, the texture coordinate, and the billboard
        /// corner. No stock vertex type carries all five.
        ///
        /// The corner is its OWN channel rather than being read back out of the texture coordinate,
        /// and that separation is what allows a card to be rotated or resized at all. While one
        /// number served as both, the image was nailed to the quad: any transform applied to it
        /// moved the card and the picture on it together, which is no transform at all.
        ///
        /// Occlusion goes in the COLOR slot because that is the one the material graph can read
        /// back without a shader of its own.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private readonly struct LeafCardVertex(
            Vector3 position, Vector3 normal, Color occlusion, Vector2 texCoord, Vector2 corner)
        {
            public readonly Vector3 Position = position;
            public readonly Vector3 Normal = normal;
            public readonly Color Occlusion = occlusion;
            public readonly Vector2 TexCoord = texCoord;
            public readonly Vector2 Corner = corner;

            public static readonly VertexDeclaration Layout = new(
                VertexElement.Position<Vector3>(),
                VertexElement.Normal<Vector3>(),
                VertexElement.Color<Color>(),
                VertexElement.TextureCoordinate<Vector2>(0),
                VertexElement.TextureCoordinate<Vector2>(1));
        }

        /// <summary>
        /// One material per detail level, because the card's size is a shader GENERIC on the
        /// billboard feature rather than a bound parameter — two sizes cannot share a compiled
        /// shader. Everything else about them is identical.
        /// </summary>
        private static Material QuadMaterial(Game game, LeafDetail detail)
        {
            if (quadMaterials.TryGetValue(detail, out var cached)) return cached;

            return quadMaterials[detail] = Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    // The card is built facing the camera but lit by the blob's outward normal, so
                    // its two sides are the same surface and either may be the one you see.
                    CullMode = CullMode.None,
                    Displacement = new MaterialTreeBillboardFeature(Cards(detail).Size),
                    // Two occlusions multiply here and they answer different questions. The texture
                    // carries per-TEXEL occlusion — which parts of a leaf clump are buried in the
                    // clump — and the vertex stream carries per-CARD occlusion, which cards are
                    // buried in the tree. Neither can see what the other sees.
                    Diffuse = new MaterialDiffuseMapFeature(
                        new ComputeBinaryColor(
                            new ComputeTextureColor(LeafTexture(game)),
                            new ComputeVertexStreamColor(),
                            BinaryOperator.Multiply)),
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
                        Alpha = new ComputeFloat(AlphaCutoff),
                    },
                },
            });
        }

        /// <summary>
        /// The PNG is a greyscale shape mask with no colour of its own. It becomes a hard cut-out:
        /// <see cref="LeafColor"/> everywhere, and an alpha thresholded to 0 or 255.
        ///
        /// The colour also carries AMBIENT OCCLUSION, per texel. The mask is the only description
        /// of the foliage's shape we have, and it is enough: blurring it answers "how much leaf
        /// surrounds this point", which is what occlusion means. A texel in the middle of a leaf
        /// mass darkens; one on an outer edge, with sky behind it, does not. Baked once at load, so
        /// it costs nothing per frame and needs no second texture.
        ///
        /// The blur wraps, because the material tiles this texture — sampling it clamped would draw
        /// a bright seam along every tile boundary.
        ///
        /// Decoded through StbImageSharp because Texture.Load pulls in Windows-only
        /// System.Drawing.Common.
        /// </summary>
        private static Texture LeafTexture(Game game)
        {
            if (leafTexture != null) return leafTexture;

            using var stream = File.OpenRead(LeafTexturePath);
            var mask = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            int width = mask.Width;
            int height = mask.Height;

            var coverage = new float[width * height];
            for (int i = 0; i < coverage.Length; i++)
                coverage[i] = mask.Data[i * 4] / 255f;

            var enclosure = Blurred(coverage, width, height);
            var pixels = new byte[width * height * 4];

            for (int i = 0; i < coverage.Length; i++)
            {
                int p = i * 4;
                float ao = 1f - AoStrength * enclosure[i];

                // The leaf colour is written EVERYWHERE, including under the texels that are about
                // to be made transparent. The sampler filters across the cutout boundary whatever
                // the alpha says, so any other colour parked in the transparent region gets dragged
                // out into a fringe around every leaf. Bleeding the colour outwards is what stops
                // that, and it is why the cards cannot also carry a second colour for the blobs.
                pixels[p + 0] = Channel(LeafColor.R, ao);
                pixels[p + 1] = Channel(LeafColor.G, ao);
                pixels[p + 2] = Channel(LeafColor.B, ao);

                // Binary, not the PNG's antialiased edge. A cutoff turns a soft edge into a hard
                // one anyway, but it does it at whatever width the source fades over — so the
                // silhouette wandered around inside that band. Thresholding at the same value the
                // material cuts at makes the two agree.
                pixels[p + 3] = coverage[i] >= AlphaCutoff ? (byte)255 : (byte)0;
            }

            return leafTexture = Texture.New2D(
                game.GraphicsDevice, width, height, PixelFormat.R8G8B8A8_UNorm_SRgb, pixels);

            static byte Channel(float color, float ao)
                => (byte)(Math.Clamp(color * ao, 0f, 1f) * 255f);
        }

        /// <summary>
        /// Repeated box blurs, which approach a gaussian and cost a fixed number of passes rather
        /// than a wide kernel. Separable and wrapping on both axes.
        /// </summary>
        private static float[] Blurred(float[] source, int width, int height)
        {
            var front = (float[])source.Clone();
            var back = new float[source.Length];

            for (int pass = 0; pass < AoBlurPasses; pass++)
            {
                Box(front, back, width, height, horizontal: true);
                Box(back, front, width, height, horizontal: false);
            }

            return front;

            static void Box(float[] src, float[] dst, int width, int height, bool horizontal)
            {
                float norm = 1f / (AoBlurRadius * 2 + 1);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float sum = 0f;
                        for (int k = -AoBlurRadius; k <= AoBlurRadius; k++)
                        {
                            int sx = horizontal ? Repeat(x + k, width) : x;
                            int sy = horizontal ? y : Repeat(y + k, height);
                            sum += src[sy * width + sx];
                        }

                        dst[y * width + x] = sum * norm;
                    }
                }
            }

            static int Repeat(int value, int size) => (value % size + size) % size;
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
