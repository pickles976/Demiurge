using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// The client's view of the terrain: the voxel data it knows about, the entity showing each
    /// SECTION, and the bookkeeping that keeps the second in sync with the first.
    ///
    /// Voxel data is owned here and edited in place through <see cref="Map"/>; anything that changes
    /// it must mark the affected region dirty and then call <see cref="RebuildDirty"/>. Those two are
    /// deliberately separate so a burst of edits in one frame costs one re-mesh, not one per edit.
    ///
    /// Sections rather than whole chunks are the unit because that's what makes an edit cheap: a dig
    /// re-meshes 16^3 voxels instead of a 16x16x128 column, and each section culls on its own box.
    /// </summary>
    public sealed class ClientTerrain
    {
        readonly Scene scene;
        readonly ChunkMeshFactory factory;
        readonly Dictionary<SectionIndex, Entity> entities = new();
        readonly HashSet<SectionIndex> dirty = new();

        public ChunkMap Map { get; } = new();

        public ClientTerrain(Scene scene, ChunkMeshFactory factory)
        {
            this.scene = scene;
            this.factory = factory;
        }

        /// <summary>
        /// Generates any chunk in the inclusive rectangle that isn't loaded, and marks all of its
        /// sections for meshing. This is the seam where server-supplied chunks will replace local
        /// noise: the rest of this class doesn't care where voxels came from.
        /// </summary>
        public void EnsureGenerated(ChunkIndex min, ChunkIndex max)
        {
            for (int x = min.x; x <= max.x; x++)
            {
                for (int z = min.z; z <= max.z; z++)
                {
                    var index = new ChunkIndex { x = x, z = z };
                    if (Map.Has(index)) continue;

                    Map.Insert(ChunkGenerator.GenerateChunk(index));
                    MarkChunkDirty(index);
                }
            }
        }

        /// <summary>Marks every section of one chunk, and anything reading into it.</summary>
        public void MarkChunkDirty(ChunkIndex index)
        {
            (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);

            MarkRegionDirty(originX, ChunkConstants.WorldMinY, originZ,
                            originX + ChunkConstants.ChunkWidth - 1, ChunkConstants.WorldMaxY - 1,
                            originZ + ChunkConstants.ChunkWidth - 1);
        }

        /// <summary>
        /// Marks every section whose mesh depends on a changed world-voxel box. The geometry lives in
        /// <see cref="ChunkMesher.CollectDependentSections"/> so the server can reuse it.
        /// </summary>
        public void MarkRegionDirty(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
            => ChunkMesher.CollectDependentSections(minX, minY, minZ, maxX, maxY, maxZ, dirty);

        /// <summary>
        /// Re-meshes everything marked dirty and swaps the entities over. Call once per frame, not
        /// once per edit. Returns how many sections were rebuilt.
        ///
        /// Sections that can't be meshed yet — because a neighbouring chunk they read isn't loaded —
        /// stay dirty and get retried next time. That's why the outer ring of a freshly generated
        /// area produces nothing until the ring beyond it exists.
        /// </summary>
        public int RebuildDirty()
        {
            if (dirty.Count == 0) return 0;

            var pending = dirty.ToArray();
            int rebuilt = 0;

            foreach (var section in pending)
            {
                if (!Map.Has(section.Chunk)) { dirty.Remove(section); continue; }
                if (!factory.TryBuild(Map, section, out Entity? entity)) continue;   // retry later

                // Detach the old entity BEFORE attaching the new one, or the stale geometry stays in
                // the scene and you get two overlapping surfaces after an edit.
                if (entities.Remove(section, out var previous)) previous.Scene = null;

                if (entity is not null)
                {
                    entity.Scene = scene;
                    entities[section] = entity;
                }

                dirty.Remove(section);
                rebuilt++;
            }

            return rebuilt;
        }
    }
}
