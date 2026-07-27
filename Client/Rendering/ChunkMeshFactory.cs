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
        /// Uploads one section's finished geometry and returns its entity, positioned in world space.
        /// Null when the mesh holds no surface, which is the common case: most sections of a column are
        /// entirely air or entirely solid.
        /// </summary>
        public Entity? Build(SectionIndex section, MeshData mesh)
        {
            if (mesh.Indices.Length == 0) return null;

            var entity = ToEntity(mesh);

            // Mesh positions are SECTION-local, so the transform carries both the chunk's ground
            // offset and the section's height.
            var origin = ChunkTransforms.ChunkOriginPosition(section.Chunk);
            entity.Transform.Position = new Vector3(origin.X, section.BaseY, origin.Z);

            return entity;
        }

        /// <summary>
        /// One Mesh per submesh so each material draws with its own texture. They all share the one
        /// vertex and index buffer and differ only in MeshDraw.StartLocation, which Stride passes
        /// straight to DrawIndexed as startIndexLocation — so this is one upload, several draws.
        /// </summary>
        Entity ToEntity(MeshData mesh)
        {
            var vertices = new VertexPositionNormalTexture[mesh.Positions.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                // UV is unused: the triplanar shader derives its own from world position. The vertex
                // format must still declare TEXCOORD0 or the stream doesn't exist for it to write.
                vertices[i] = new VertexPositionNormalTexture(mesh.Positions[i], mesh.Normals[i], Vector2.Zero);
            }

            var vertexBuffer = Stride.Graphics.Buffer.Vertex.New(game.GraphicsDevice, vertices, GraphicsResourceUsage.Default);
            var indexBuffer = Stride.Graphics.Buffer.Index.New(game.GraphicsDevice, mesh.Indices);

            var vertexBinding = new VertexBufferBinding(vertexBuffer, VertexPositionNormalTexture.Layout, vertices.Length);
            var indexBinding = new IndexBufferBinding(indexBuffer, is32Bit: true, mesh.Indices.Length);

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
                        StartLocation = submesh.Start,
                        IndexBuffer = indexBinding,
                        VertexBuffers = [vertexBinding],
                    },
                    MaterialIndex = model.Materials.Count,
                    BoundingBox = bounds,
                });

                model.Add(new MaterialInstance(materials.For(submesh.Material)));
            }

            return new Entity("ChunkSection") { new ModelComponent(model) };
        }
    }
}
