using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// VIEW of the terrain: turns the voxels in <see cref="GameClient.TerrainState"/> into one entity
    /// per section, and keeps them in sync as chunks arrive or change.
    ///
    /// It does not own the voxels and never generates any — the Sim layer holds what the server sent.
    /// Anything that changes them marks the affected region dirty and then calls
    /// <see cref="RebuildDirty"/>; those two are deliberately separate so a burst of edits in one
    /// frame costs one re-mesh, not one per edit.
    ///
    /// Sections rather than whole chunks are the unit because that's what makes an edit cheap: a dig
    /// re-meshes 16^3 voxels instead of a 16x16x128 column, and each section culls on its own box.
    /// </summary>
    public sealed class ClientTerrain
    {
        readonly Scene scene;
        readonly ChunkMeshFactory factory;
        readonly GameClient.TerrainState terrain;
        readonly Dictionary<SectionIndex, Entity> entities = new();
        readonly HashSet<SectionIndex> dirty = new();

        ChunkMap Map => terrain.Map;

        public ClientTerrain(Scene scene, ChunkMeshFactory factory, GameClient.TerrainState terrain)
        {
            this.scene = scene;
            this.factory = factory;
            this.terrain = terrain;

            // A chunk becomes meshable when its last slab arrives — and so do its neighbours, whose
            // aprons read into it, which MarkChunkDirty already accounts for.
            terrain.ChunkCompleted += MarkChunkDirty;
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
        /// Sections meshed per call. A cap, not a target: streaming a map marks hundreds of sections
        /// dirty at once, and meshing them all in one frame stalls the thread that also dispatches
        /// incoming chunk messages — which throttles the very arrivals it's reacting to. Whatever is
        /// left stays dirty for the next frame.
        /// </summary>
        const int MaxRebuildsPerCall = 4;

        /// <summary>
        /// Re-meshes up to <see cref="MaxRebuildsPerCall"/> dirty sections and swaps the entities over.
        /// Call once per frame, not once per edit. Returns how many were rebuilt.
        ///
        /// Sections that can't be meshed yet — because a neighbouring chunk they read hasn't arrived —
        /// stay dirty and get retried next time. That's why the outer ring of the loaded area produces
        /// nothing until the ring beyond it exists.
        /// </summary>
        public int RebuildDirty()
        {
            if (dirty.Count == 0) return 0;

            var pending = dirty.ToArray();
            int rebuilt = 0;

            foreach (var section in pending)
            {
                if (rebuilt >= MaxRebuildsPerCall) break;

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
