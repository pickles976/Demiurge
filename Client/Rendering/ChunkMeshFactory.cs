using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Demiurge
{
    /// <summary>
    /// Geometry in, Stride entity out — the last leg of the voxels-to-GPU pipeline:
    ///
    ///     ChunkMap + SectionIndex  ->  scratch buffer  ->  MeshData  ->  crease split  ->  Entity
    ///                                  \___ SectionMeshQueue, on workers ___/    \_ here, main thread _/
    ///
    /// This is the only place that knows both <see cref="MeshData"/> and Stride's buffer types.
    /// Common produces engine-agnostic System.Numerics geometry; the conversion lives here so the
    /// mesher stays testable headlessly and usable by the server.
    ///
    /// MAIN THREAD ONLY, and unlike the rest of the pipeline that is not a convention — it creates GPU
    /// buffers. Everything upstream of it runs on <see cref="SectionMeshQueue"/>'s workers.
    /// </summary>
    public sealed class ChunkMeshFactory
    {
        readonly Game game;
        readonly TerrainMaterials materials;

        public ChunkMeshFactory(Game game, TerrainMaterials materials)
        {
            this.game = game;
            this.materials = materials;
        }

        /// <summary>
        /// Ticks the last <see cref="UploadBatch"/> spent inside GPU buffer creation, as opposed to CPU
        /// prep. Read by ClientTerrain's diagnostics — measuring this is what finally identified the
        /// bottleneck after three wrong guesses.
        /// </summary>
        public long LastGpuTicks { get; private set; }

        /// <summary>
        /// Uploads several sections into ONE vertex/index buffer pair and returns an entity for each.
        ///
        /// Batching is not a micro-optimisation here. Every `Buffer.New` is its own Vulkan device
        /// allocation, and the measured cost per allocation CLIMBS as the live count grows — 2 ms early,
        /// 11.7 ms and still rising by the time a world is half loaded. Per-section buffers meant ~3,468
        /// allocations, near the 4096 cap many drivers impose. One pair per batch of 64 takes that to
        /// roughly 30, which keeps every allocation in the cheap regime.
        ///
        /// Note the batch axis is "whatever finished this frame", not a spatial region. Only ~14% of
        /// sections hold geometry, so a chunk column averages 1.14 of them and batching by column would
        /// have bought almost nothing — and it would have coupled sections that re-mesh independently.
        ///
        /// Indices are rewritten GLOBAL to the shared buffer rather than using VertexBufferBinding's
        /// vertexOffset, whose documentation says "in Vertex ElementCount" while the backing API takes
        /// bytes. Getting that wrong renders garbage rather than failing.
        /// </summary>
        public SectionBuffers UploadBatch(
            IReadOnlyList<(LodSection Section, MeshData Mesh)> batch, List<Entity> entities)
        {
            int vertexCount = 0, indexCount = 0;
            foreach (var (_, mesh) in batch)
            {
                vertexCount += mesh.Positions.Length;
                indexCount += mesh.Indices.Length;
            }

            var vertices = new VertexPositionNormalTexture[vertexCount];
            var indices = new int[indexCount];
            var indexStarts = new int[batch.Count];

            int v = 0, i = 0;

            for (int b = 0; b < batch.Count; b++)
            {
                var mesh = batch[b].Mesh;
                indexStarts[b] = i;

                for (int k = 0; k < mesh.Positions.Length; k++)
                {
                    // UV is unused: the triplanar shader derives its own from world position. The vertex
                    // format must still declare TEXCOORD0 or the stream doesn't exist for it to write.
                    vertices[v + k] = new VertexPositionNormalTexture(mesh.Positions[k], mesh.Normals[k], Vector2.Zero);
                }

                for (int k = 0; k < mesh.Indices.Length; k++)
                    indices[i + k] = mesh.Indices[k] + v;

                v += mesh.Positions.Length;
                i += mesh.Indices.Length;
            }

            long gpuStart = System.Diagnostics.Stopwatch.GetTimestamp();

            var vertexBuffer = Stride.Graphics.Buffer.Vertex.New(game.GraphicsDevice, vertices, GraphicsResourceUsage.Default);
            var indexBuffer = Stride.Graphics.Buffer.Index.New(game.GraphicsDevice, indices);

            LastGpuTicks = System.Diagnostics.Stopwatch.GetTimestamp() - gpuStart;

            var buffers = new SectionBuffers(vertexBuffer, indexBuffer);

            var vertexBinding = new VertexBufferBinding(vertexBuffer, VertexPositionNormalTexture.Layout, vertexCount);
            var indexBinding = new IndexBufferBinding(indexBuffer, is32Bit: true, indexCount);

            for (int b = 0; b < batch.Count; b++)
            {
                entities.Add(BuildEntity(batch[b].Section, batch[b].Mesh, indexStarts[b], vertexBinding, indexBinding));
                buffers.AddRef();
            }

            return buffers;
        }

        Entity BuildEntity(LodSection section, MeshData mesh, int indexStart,
                           VertexBufferBinding vertexBinding, IndexBufferBinding indexBinding)
        {
            // Whole-mesh bounds for every submesh: conservative, so culling is coarser than it could
            // be. Leave it empty and the mesh is frustum-culled every frame, silently.
            var bounds = BoundingBox.FromPoints(Array.ConvertAll(mesh.Positions, p => (Vector3)p));

            var model = new Model();

            foreach (var submesh in mesh.Submeshes)
            {
                model.Add(new Mesh
                {
                    Draw = new MeshDraw
                    {
                        PrimitiveType = PrimitiveType.TriangleList,
                        DrawCount = submesh.Count,
                        StartLocation = indexStart + submesh.Start,
                        IndexBuffer = indexBinding,
                        VertexBuffers = [vertexBinding],
                    },
                    MaterialIndex = model.Materials.Count,
                    BoundingBox = bounds,
                });

                model.Add(new MaterialInstance(materials.For(submesh.Material)));
            }

            // Mesh positions are in the box's own CELL units, so the transform carries both where the
            // box starts and how big its cells are. Uniform scale, so the normals stay correct.
            var entity = new Entity("ChunkSection") { new ModelComponent(model) };

            entity.Transform.Position = new Vector3(section.OriginX, section.OriginY, section.OriginZ);
            entity.Transform.Scale = new Vector3(section.Stride);

            return entity;
        }
    }

    /// <summary>
    /// One vertex+index buffer pair shared by every section uploaded in the same batch.
    ///
    /// Reference counted because sections re-mesh independently: a batch's buffers must survive until
    /// the LAST of its sections has been replaced. Nothing disposed these before, which leaked a buffer
    /// pair on every re-mesh — and since a section is re-dirtied as each of its nine neighbouring chunks
    /// completes, that was most of them, several times over.
    ///
    /// Main thread only, so the count needs no interlocking.
    /// </summary>
    public sealed class SectionBuffers(Stride.Graphics.Buffer vertex, Stride.Graphics.Buffer index)
    {
        int references;

        public void AddRef() => references++;

        public void Release()
        {
            if (--references > 0) return;

            vertex.Dispose();
            index.Dispose();
        }
    }
}
