using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using System.Runtime.InteropServices;
using GpuBuffer = Stride.Graphics.Buffer;

namespace Demiurge
{
    /// <summary>
    /// Chunk-streamed grass renderer. Each terrain chunk owns one plain procedural mesh, allowing the
    /// follower to replace only chunks entering or leaving its radius. Chunk builds are spread across
    /// frames so walking never regenerates and uploads the entire field at once.
    /// </summary>
    public sealed class GpuGrassRenderer : IDisposable
    {
        public const int DefaultMaxInstances = 200_000;
        private const int BladesPerSeed = 60;
        private const int DensityQuantum = 5;
        private const int MaxChunkUploadsPerFrame = 1;
        private const float BuriedDepth = 0.25f;

        private static readonly ValueParameterKey<float> KeyGrassColorScale =
            ParameterKeys.NewValue<float>(1f, "GrassDiffuse.GrassColorScale");

        private readonly Entity grassEntity;
        private readonly GraphicsDevice graphicsDevice;
        private readonly Material material;
        private readonly Dictionary<Int3, ChunkRender> chunks = new();

        private bool loggedFirstBuild;
        private int liveInstanceCount;
        private readonly int maxInstances;
        private float grassRadius = 45f;

        public Entity GrassEntity => grassEntity;

        public GpuGrassRenderer(IServiceRegistry services, GraphicsDevice graphicsDevice, int maxInstances = DefaultMaxInstances)
        {
            this.graphicsDevice = graphicsDevice;
            this.maxInstances = Math.Max(1_000, maxInstances);

            var grassColor = new ComputeShaderClassColor { MixinReference = "GrassDiffuse" };
            material = Material.New(graphicsDevice, new MaterialDescriptor
            {
                Attributes =
                {
                    Diffuse = new MaterialDiffuseMapFeature(grassColor),
                    DiffuseModel = new MaterialDiffuseWrappedModelFeature(wrap: 0.35f),
                },
            });
            // Stride's MaterialSurfaceLightingAndShading flips normalWS when IsFrontFace is false.
            // One two-sided card with its real geometric normal therefore gets +N on one visible
            // side and -N on the other without duplicating geometry.
            material.Passes[0].CullMode = CullMode.None;
            material.Passes[0].Parameters.Set(KeyGrassColorScale, 1f);

            grassEntity = new Entity("GrassPool");
        }

        public void SetGrassDistance(float radius) => grassRadius = Math.Max(1f, radius);
        public void SetLodParams(float multiplier, float exponent) { }
        public void SetWind(float strength, float speed, float frequency) { }
        public void ClearTrampleSources() { }
        public void AddTrampleSource(Vector3 worldPosition, float radius) { }

        public void SetChunkSeeds(Int3 key, ReadOnlySpan<GrassSeed> seeds)
        {
            if (seeds.Length == 0)
            {
                RemoveChunk(key);
                return;
            }

            if (!chunks.TryGetValue(key, out var chunk))
            {
                chunk = new ChunkRender(key);
                chunks.Add(key, chunk);
            }

            chunk.Seeds = seeds.ToArray();
            chunk.Dirty = true;
        }

        public void RemoveChunk(Int3 key)
        {
            if (!chunks.Remove(key, out var chunk)) return;

            liveInstanceCount -= chunk.BladeCount;
            chunk.Dispose();
        }

        public void ClearSeeds()
        {
            if (chunks.Count == 0) return;

            foreach (var chunk in chunks.Values) chunk.Dispose();
            chunks.Clear();
            liveInstanceCount = 0;
        }

        public void Update(Vector3 cameraPosition, Game game)
        {
            foreach (var chunk in chunks.Values)
            {
                int target = DesiredBladesPerSeed(chunk.Key, cameraPosition);
                if (target == chunk.TargetBladesPerSeed) continue;

                chunk.TargetBladesPerSeed = target;
                chunk.Dirty = true;
            }

            for (int upload = 0; upload < MaxChunkUploadsPerFrame; upload++)
            {
                ChunkRender? next = null;
                float nearestSq = float.MaxValue;

                foreach (var chunk in chunks.Values)
                {
                    if (!chunk.Dirty) continue;

                    float centerX = chunk.Key.X * ChunkConstants.ChunkWidth + ChunkConstants.ChunkWidth * 0.5f;
                    float centerZ = chunk.Key.Z * ChunkConstants.ChunkWidth + ChunkConstants.ChunkWidth * 0.5f;
                    float dx = centerX - cameraPosition.X;
                    float dz = centerZ - cameraPosition.Z;
                    float distSq = dx * dx + dz * dz;
                    if (distSq >= nearestSq) continue;

                    nearestSq = distSq;
                    next = chunk;
                }

                if (next == null) break;
                RebuildChunk(next);
            }
        }

        private int DesiredBladesPerSeed(Int3 key, Vector3 cameraPosition)
        {
            float centerX = key.X * ChunkConstants.ChunkWidth + ChunkConstants.ChunkWidth * 0.5f;
            float centerZ = key.Z * ChunkConstants.ChunkWidth + ChunkConstants.ChunkWidth * 0.5f;
            float dx = centerX - cameraPosition.X;
            float dz = centerZ - cameraPosition.Z;
            float density = MathUtil.Clamp(1f - MathF.Sqrt(dx * dx + dz * dz) / grassRadius, 0f, 1f);
            int raw = (int)MathF.Ceiling(BladesPerSeed * density * density);
            if (raw == 0) return 0;

            return Math.Min(BladesPerSeed, ((raw + DensityQuantum - 1) / DensityQuantum) * DensityQuantum);
        }

        private void RebuildChunk(ChunkRender chunk)
        {
            int available = Math.Max(0, maxInstances - (liveInstanceCount - chunk.BladeCount));
            int capacity = Math.Min(available, chunk.Seeds.Length * chunk.TargetBladesPerSeed);
            var vertices = new List<GrassVertex>(capacity * 3);
            var indices = new List<int>(capacity * 3);
            int bladeCount = 0;

            foreach (var seed in chunk.Seeds)
            {
                uint hash = seed.Variation;
                for (int i = 0; i < chunk.TargetBladesPerSeed && bladeCount < available; i++)
                {
                    hash = WangHash(hash + (uint)i);
                    float jitterX = (hash & 0xFFFF) / 65535f;
                    hash = WangHash(hash);
                    float jitterZ = (hash & 0xFFFF) / 65535f;
                    hash = WangHash(hash);
                    float randomScale = (hash & 0xFFFF) / 65535f;
                    hash = WangHash(hash);
                    float randomAngle = (hash & 0xFFFF) / 65535f;

                    float scale = 0.55f + randomScale * 0.45f;
                    var position = new Vector3(seed.Position.X + jitterX, seed.Position.Y, seed.Position.Z + jitterZ);
                    AddBlade(vertices, indices, position, randomAngle * MathUtil.Pi, scale);
                    bladeCount++;
                }

                if (bladeCount >= available) break;
            }

            ReplaceChunkMesh(chunk, vertices, indices);
            liveInstanceCount += bladeCount - chunk.BladeCount;
            chunk.BladeCount = bladeCount;
            chunk.Dirty = false;

            if (!loggedFirstBuild && liveInstanceCount > 0)
            {
                Stride.Core.Diagnostics.GlobalLogger.GetLogger("Vegetation")
                    .Info($"grass: chunked upload, {liveInstanceCount} live blades");
                loggedFirstBuild = true;
            }
        }

        private void ReplaceChunkMesh(ChunkRender chunk, List<GrassVertex> vertices, List<int> indices)
        {
            var model = new Model();
            model.Add(new MaterialInstance(material));
            GpuBuffer? newVertexBuffer = null;
            GpuBuffer? newIndexBuffer = null;

            if (vertices.Count > 0 && indices.Count > 0)
            {
                newVertexBuffer = GpuBuffer.Vertex.New(graphicsDevice, vertices.ToArray(), GraphicsResourceUsage.Default);
                newIndexBuffer = GpuBuffer.Index.New(graphicsDevice, indices.ToArray());
                var bounds = BoundingBox.FromPoints(vertices.Select(v => v.Position).ToArray());

                model.Add(new Mesh
                {
                    Draw = new MeshDraw
                    {
                        PrimitiveType = PrimitiveType.TriangleList,
                        DrawCount = indices.Count,
                        IndexBuffer = new IndexBufferBinding(newIndexBuffer, is32Bit: true, indices.Count),
                        VertexBuffers = [new VertexBufferBinding(newVertexBuffer, GrassVertex.Layout, vertices.Count)],
                    },
                    MaterialIndex = 0,
                    BoundingBox = bounds,
                    BoundingSphere = BoundingSphere.FromBox(bounds),
                });
            }

            if (chunk.Entity == null)
            {
                chunk.ModelComponent = new ModelComponent { IsShadowCaster = false };
                chunk.Entity = new Entity($"GrassChunk({chunk.Key.X},{chunk.Key.Z})")
                {
                    chunk.ModelComponent,
                };
                chunk.Entity.Scene = grassEntity.Scene;
            }

            chunk.ModelComponent!.Model = model;
            chunk.VertexBuffer?.Dispose();
            chunk.IndexBuffer?.Dispose();
            chunk.VertexBuffer = newVertexBuffer;
            chunk.IndexBuffer = newIndexBuffer;
        }

        private static void AddBlade(List<GrassVertex> vertices, List<int> indices, Vector3 position, float yaw, float scale)
        {
            float visibleHeight = scale * 0.45f;
            float width = visibleHeight * (0.22f / 0.90f);
            float c = MathF.Cos(yaw);
            float s = MathF.Sin(yaw);
            var right = new Vector3(c, 0f, -s) * (width * 0.5f);
            var root = position - Vector3.UnitY * BuriedDepth;
            var bottomLeft = root - right;
            var bottomRight = root + right;
            var top = root + Vector3.UnitY * (visibleHeight + BuriedDepth);
            // right x up is the card's geometric normal. It stays in world space because the grass
            // entity has no rotation.
            var normal = new Vector3(s, 0f, c);
            int front = vertices.Count;

            vertices.Add(new GrassVertex(bottomLeft, normal, new Vector2(0f, 1f)));
            vertices.Add(new GrassVertex(bottomRight, normal, new Vector2(1f, 1f)));
            vertices.Add(new GrassVertex(top, normal, new Vector2(0.5f, 0f)));
            // Stride's front face is clockwise: from +normal, these have cross products pointing
            // away from the viewer (-normal).
            indices.AddRange([front, front + 2, front + 1]);
        }

        private static uint WangHash(uint x)
        {
            unchecked
            {
                x = (x ^ 61u) ^ (x >> 16);
                x *= 9u;
                x ^= x >> 4;
                x *= 0x27d4eb2du;
                x ^= x >> 15;
                return x;
            }
        }

        public void Dispose()
        {
            ClearSeeds();
            grassEntity.Scene = null;
        }

        private sealed class ChunkRender(Int3 key) : IDisposable
        {
            public Int3 Key { get; } = key;
            public GrassSeed[] Seeds { get; set; } = [];
            public Entity? Entity { get; set; }
            public ModelComponent? ModelComponent { get; set; }
            public GpuBuffer? VertexBuffer { get; set; }
            public GpuBuffer? IndexBuffer { get; set; }
            public int TargetBladesPerSeed { get; set; } = -1;
            public int BladeCount { get; set; }
            public bool Dirty { get; set; } = true;

            public void Dispose()
            {
                Entity?.Scene = null;
                VertexBuffer?.Dispose();
                IndexBuffer?.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct GrassVertex
        {
            public readonly Vector3 Position;
            public readonly Vector3 Normal;
            public readonly Vector2 TexCoord;

            public static readonly VertexDeclaration Layout = new(
                VertexElement.Position<Vector3>(),
                VertexElement.Normal<Vector3>(),
                VertexElement.TextureCoordinate<Vector2>());

            public GrassVertex(Vector3 position, Vector3 normal, Vector2 texCoord)
            {
                Position = position;
                Normal = normal;
                TexCoord = texCoord;
            }
        }
    }
}
