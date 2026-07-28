using Demiurge.GameClient;
using NoiseDotNet;
using System.Collections.Concurrent;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using Buffer = Stride.Graphics.Buffer;

namespace Demiurge
{
    public static class TreeViewFactory
    {
        private const string ModelPath = "assets/models/tree.gltf";
        private const int AnchorCount = 5;
        private const float TreeScale = 2f;
        private const float LeafWidth = 1.25f;
        private const float LeafHeight = 1.0f;

        private static Model? treeModel;
        private static Model? leafModel;
        private static Material? leafMaterial;

        public static Entity Create(Game game, ModelLocators locators)
        {
            var root = new Entity
            {
                new ModelComponent(TreeModel(game))
            };

            var leaves = CreateLeaves(game, locators);
            root.Transform.Children.Add(leaves.Transform);
            root.Transform.Scale = new Vector3(TreeScale);

            return root;
        }

        /// <summary>
        /// Keeps all authoritative tree objects but only instantiates nearby views. The GLTF contains
        /// five mesh primitives, so rendering all 1 km of trees would cost thousands of draw calls.
        /// </summary>
        public sealed class Manager
        {
            private const float SpawnRadius = 140f;
            private const float DespawnRadius = 165f;
            private const float RefreshDistance = 8f;

            private readonly Game game;
            private readonly Scene scene;
            private readonly PlayerRegistry players;
            private readonly ModelLocators locators;
            private readonly ConcurrentDictionary<uint, NetObject> objects = new();
            private readonly ConcurrentQueue<uint> removed = new();
            private readonly Dictionary<uint, Entity> views = new();

            private Vector3 lastPosition;
            private bool hasLastPosition;
            private bool loggedFirstRefresh;
            private volatile bool dirty;

            public Manager(Game game, Scene scene, PlayerRegistry players, ModelLocators locators)
            {
                this.game = game;
                this.scene = scene;
                this.players = players;
                this.locators = locators;

                new Entity("TreeViewManager")
                {
                    new TreeViewManagerScript { Manager = this },
                }.Scene = scene;
            }

            public void Add(NetObject tree)
            {
                objects[tree.NetworkId] = tree;
                dirty = true;
            }

            public void Remove(uint networkId)
            {
                objects.TryRemove(networkId, out _);
                removed.Enqueue(networkId);
                dirty = true;
            }

            public void Update()
            {
                while (removed.TryDequeue(out uint networkId))
                    RemoveView(networkId);

                if (players.LocalPlayer is not { } local) return;

                var position = local.Position.ToStride();
                float moveX = position.X - lastPosition.X;
                float moveZ = position.Z - lastPosition.Z;
                if (!dirty && hasLastPosition &&
                    moveX * moveX + moveZ * moveZ < RefreshDistance * RefreshDistance) return;

                dirty = false;
                hasLastPosition = true;
                lastPosition = position;

                float spawnSq = SpawnRadius * SpawnRadius;
                float despawnSq = DespawnRadius * DespawnRadius;

                foreach (var (networkId, tree) in objects)
                {
                    float dx = tree.Transform.Position.X - position.X;
                    float dz = tree.Transform.Position.Z - position.Z;
                    float distanceSq = dx * dx + dz * dz;

                    if (views.ContainsKey(networkId))
                    {
                        if (distanceSq > despawnSq) RemoveView(networkId);
                        continue;
                    }

                    if (distanceSq > spawnSq) continue;

                    var view = Create(game, locators);
                    view.Name = $"NetObject_{networkId}";
                    view.Transform.Position = tree.Transform.Position.ToStride();
                    view.Transform.Rotation = Quaternion.RotationY(tree.Transform.Yaw);
                    view.Scene = scene;
                    views.Add(networkId, view);
                }

                if (!loggedFirstRefresh && objects.Count > 0)
                {
                    Stride.Core.Diagnostics.GlobalLogger.GetLogger("Vegetation")
                        .Info($"trees: {views.Count} nearby views from {objects.Count} replicated trees");
                    loggedFirstRefresh = true;
                }
            }

            private void RemoveView(uint networkId)
            {
                if (!views.Remove(networkId, out var view)) return;
                view.Scene = null;
            }
        }

        public sealed class TreeViewManagerScript : SyncScript
        {
            public required Manager Manager { get; init; }
            public override void Update() => Manager.Update();
        }

        private static Model TreeModel(Game game)
            => treeModel ??= GLTFLoader.LoadModel(game, ModelPath);

        private static Entity CreateLeaves(Game game, ModelLocators locators)
            => new("TreeLeaves") { new ModelComponent(LeafModel(game, locators)) };

        private static Model LeafModel(Game game, ModelLocators locators)
        {
            if (leafModel != null) return leafModel;

            var anchors = Anchors(locators);
            var vertices = new VertexPositionNormalTexture[anchors.Count * 4];
            var indices = new int[anchors.Count * 6];

            for (int i = 0; i < anchors.Count; i++)
            {
                var center = anchors[i].ToStride();
                var normal = new Vector3(center.X, 0f, center.Z);
                if (normal.LengthSquared() < 1e-6f)
                {
                    float angle = i / (float)anchors.Count * MathUtil.TwoPi;
                    normal = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                }
                normal.Normalize();

                var right = Vector3.Cross(Vector3.UnitY, normal);
                right.Normalize();
                float scale = 0.9f + Hash01(i, 0) * 0.3f;
                float halfW = LeafWidth * scale * 0.5f;
                float halfH = LeafHeight * scale * 0.5f;

                int v = i * 4;
                vertices[v + 0] = new VertexPositionNormalTexture(center - right * halfW - Vector3.UnitY * halfH, normal, new Vector2(0f, 1f));
                vertices[v + 1] = new VertexPositionNormalTexture(center + right * halfW - Vector3.UnitY * halfH, normal, new Vector2(1f, 1f));
                vertices[v + 2] = new VertexPositionNormalTexture(center + right * halfW + Vector3.UnitY * halfH, normal, new Vector2(1f, 0f));
                vertices[v + 3] = new VertexPositionNormalTexture(center - right * halfW + Vector3.UnitY * halfH, normal, new Vector2(0f, 0f));

                int ix = i * 6;
                indices[ix + 0] = v + 0;
                indices[ix + 1] = v + 2;
                indices[ix + 2] = v + 1;
                indices[ix + 3] = v + 0;
                indices[ix + 4] = v + 3;
                indices[ix + 5] = v + 2;
            }

            var vertexBuffer = Buffer.Vertex.New(game.GraphicsDevice, vertices, GraphicsResourceUsage.Default);
            var indexBuffer = Buffer.Index.New(game.GraphicsDevice, indices);
            var bounds = BoundingBox.FromPoints(Array.ConvertAll(vertices, v => v.Position));
            leafModel = new Model();
            leafModel.Add(new Mesh
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
            leafModel.Add(new MaterialInstance(LeafMaterial(game)));

            return leafModel;
        }

        private static List<System.Numerics.Vector3> Anchors(ModelLocators locators)
        {
            var anchors = new List<System.Numerics.Vector3>(AnchorCount);
            for (int i = 1; i <= AnchorCount; i++)
                anchors.Add(locators.Require(ModelPath, $"node_{i}").Translation);
            return anchors;
        }

        private static Material LeafMaterial(Game game)
            => leafMaterial ??= Material.New(game.GraphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    CullMode = CullMode.None,
                    Diffuse = new MaterialDiffuseMapFeature(
                        new ComputeTextureColor(CreateLeafTexture(game)) { Filtering = TextureFilter.Point }),
                    DiffuseModel = new MaterialDiffuseWrappedModelFeature(wrap: 0.3f),
                    Transparency = new MaterialTransparencyCutoffFeature
                    {
                        Alpha = new ComputeFloat(0.08f),
                    },
                },
            });

        private static Texture CreateLeafTexture(Game game)
        {
            const int size = 128;
            var xs = new float[size * size];
            var ys = new float[size * size];
            for (int y = 0, i = 0; y < size; y++)
                for (int x = 0; x < size; x++, i++)
                {
                    xs[i] = x;
                    ys[i] = y;
                }

            var noise = new float[xs.Length];
            Noise.GradientNoise2DFractal(xs, ys, noise, new NoiseSettings
            {
                XFrequency = 1f / 34f,
                YFrequency = 1f / 34f,
                Amplitude = 1f,
                Seed = NoiseGen.Seed + 800,
            }, new FractalSettings(octaves: 4, persistence: 0.55f, lacunarity: 2f));

            var pixels = new byte[size * size * 4];
            for (int y = 0, i = 0; y < size; y++)
                for (int x = 0; x < size; x++, i++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float radius = MathF.Sqrt(u * u + v * v);
                    float n = Math.Clamp(noise[i] / 1.5f / 0.70f, -1f, 1f);
                    float shape = 1f - SmoothStep(0.55f + n * 0.08f, 0.95f + n * 0.08f, radius);
                    float holes = n > -0.25f ? 1f : 0f;
                    byte alpha = (byte)(255f * Math.Clamp(shape * holes, 0f, 1f));

                    int p = i * 4;
                    pixels[p + 0] = (byte)(34 + Math.Clamp((n + 1f) * 0.5f, 0f, 1f) * 36f);
                    pixels[p + 1] = (byte)(104 + Math.Clamp((n + 1f) * 0.5f, 0f, 1f) * 80f);
                    pixels[p + 2] = (byte)(39 + Math.Clamp((n + 1f) * 0.5f, 0f, 1f) * 24f);
                    pixels[p + 3] = alpha;
                }

            return Texture.New2D(game.GraphicsDevice, size, size, PixelFormat.R8G8B8A8_UNorm_SRgb, pixels);
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

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
