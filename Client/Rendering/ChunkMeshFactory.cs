using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;

namespace Demiurge
{
    /// <summary>
    /// Section in, Stride entity out. The whole voxels-to-GPU pipeline, in order:
    ///
    ///     ChunkMap + SectionIndex  ->  scratch buffer  ->  MeshData  ->  crease split  ->  Entity
    ///
    /// This is the only place that knows both <see cref="MeshData"/> and Stride's buffer types.
    /// Common produces engine-agnostic System.Numerics geometry; the conversion lives here so the
    /// mesher stays testable headlessly and usable by the server.
    ///
    /// NOT thread safe: the scratch buffer is reused across calls. One factory per meshing thread.
    /// </summary>
    public sealed class ChunkMeshFactory
    {
        /// <summary>
        /// Above this angle between adjacent faces, the shared vertex is split so each side gets its
        /// own normal. Raise it if smooth terrain looks faceted, lower it if creases look soft.
        /// </summary>
        const float CreaseAngleDegrees = 50f;

        readonly Game game;
        readonly TerrainMaterials materials;

        // Reused rather than allocated per section; 21^3 samples, ~74 KB.
        readonly Sample[] scratch = new Sample[ChunkMesher.ScratchVolume];

        public ChunkMeshFactory(Game game, TerrainMaterials materials)
        {
            this.game = game;
            this.materials = materials;
        }

        /// <summary>
        /// Builds the entity for one section, already positioned in world space.
        ///
        /// Returns false when the section cannot be meshed YET because a neighbouring chunk it needs
        /// isn't loaded — the caller should leave it dirty and try again. Returns true with a null
        /// entity when it meshed fine but holds no surface, which is the common case: most sections
        /// of a column are entirely air or entirely solid, and those cost only the slab scan.
        /// </summary>
        public bool TryBuild(ChunkMap map, SectionIndex section, out Entity? entity)
        {
            entity = null;

            if (!ChunkMesher.TryFillScratch(map, section, scratch)) return false;

            MeshData mesh = ChunkMesher.GenerateMeshDualContouring(scratch);
            if (mesh.Indices.Length == 0) return true;

            // DC puts a crease vertex in the right place; this gives it one normal per side so the
            // corner reads as an edge rather than a smooth blend.
            mesh = ChunkMesher.SplitCreases(mesh, CreaseAngleDegrees);

            entity = ToEntity(mesh);

            // Mesh positions are SECTION-local, so the transform carries both the chunk's ground
            // offset and the section's height.
            var origin = ChunkTransforms.ChunkOriginPosition(section.Chunk);
            entity.Transform.Position = new Vector3(origin.X, section.BaseY, origin.Z);
            return true;
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
