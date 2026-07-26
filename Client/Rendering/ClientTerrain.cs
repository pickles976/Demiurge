using System.Diagnostics;
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
        readonly Queue<SectionIndex> dirtyQueue = new();
        readonly HashSet<SectionIndex> dirtySet = new();
        readonly DirtySectionSink dirtySections;

        ChunkMap Map => terrain.Map;

        public ClientTerrain(Scene scene, ChunkMeshFactory factory, GameClient.TerrainState terrain)
        {
            this.scene = scene;
            this.factory = factory;
            this.terrain = terrain;
            dirtySections = new DirtySectionSink(this);

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
            => ChunkMesher.CollectDependentSections(minX, minY, minZ, maxX, maxY, maxZ, dirtySections);

        /// <summary>
        /// Time spent meshing per call. A budget, not a hard cap: the check happens before each
        /// section so the call can overshoot by one expensive section. Streaming a map marks hundreds
        /// of sections dirty at once, and meshing them all in one frame stalls the thread that also
        /// dispatches incoming chunk messages — which throttles the very arrivals it's reacting to.
        /// Whatever is left stays queued for the next frame.
        /// </summary>
        const double RebuildBudgetSeconds = 0.004;

        static readonly long RebuildBudgetTicks = Math.Max(1, (long)(Stopwatch.Frequency * RebuildBudgetSeconds));

        /// <summary>
        /// Re-meshes dirty sections until the time budget is spent and swaps the entities over. Call
        /// once per frame, not once per edit. Returns how many were rebuilt.
        ///
        /// Sections that can't be meshed yet — because a neighbouring chunk they read hasn't arrived —
        /// are moved to the back of the queue and retried next frame. That's why the outer ring of the
        /// loaded area produces nothing until the ring beyond it exists.
        /// </summary>
        public int RebuildDirty()
        {
            if (dirtyQueue.Count == 0) return 0;

            long start = Stopwatch.GetTimestamp();
            int attemptsRemaining = dirtyQueue.Count;
            int attempted = 0;
            int rebuilt = 0;

            while (attemptsRemaining-- > 0 && dirtyQueue.Count > 0)
            {
                // Always rebuild at least one meshable section. If a frame is already slow for
                // unrelated reasons, meshing still makes forward progress instead of starving
                // indefinitely; blocked sections are still capped by attemptsRemaining.
                if (rebuilt > 0 && Stopwatch.GetTimestamp() - start >= RebuildBudgetTicks) break;

                var section = dirtyQueue.Dequeue();
                attempted++;

                if (!Map.Has(section.Chunk)) { dirtySet.Remove(section); continue; }
                if (!factory.TryBuild(Map, section, out Entity? entity))
                {
                    dirtyQueue.Enqueue(section);
                    continue;   // retry next frame; don't spin on missing neighbours this frame
                }

                // Detach the old entity BEFORE attaching the new one, or the stale geometry stays in
                // the scene and you get two overlapping surfaces after an edit.
                if (entities.Remove(section, out var previous)) previous.Scene = null;

                if (entity is not null)
                {
                    entity.Scene = scene;
                    entities[section] = entity;
                }

                dirtySet.Remove(section);
                rebuilt++;
            }

            return rebuilt;
        }

        void EnqueueDirty(SectionIndex section)
        {
            if (!dirtySet.Add(section)) return;
            dirtyQueue.Enqueue(section);
        }

        sealed class DirtySectionSink(ClientTerrain owner) : ICollection<SectionIndex>
        {
            public int Count => owner.dirtySet.Count;
            public bool IsReadOnly => false;

            public void Add(SectionIndex item) => owner.EnqueueDirty(item);
            public void Clear()
            {
                owner.dirtyQueue.Clear();
                owner.dirtySet.Clear();
            }
            public bool Contains(SectionIndex item) => owner.dirtySet.Contains(item);
            public void CopyTo(SectionIndex[] array, int arrayIndex) => owner.dirtySet.CopyTo(array, arrayIndex);
            public bool Remove(SectionIndex item) => throw new NotSupportedException();
            public IEnumerator<SectionIndex> GetEnumerator() => owner.dirtySet.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
