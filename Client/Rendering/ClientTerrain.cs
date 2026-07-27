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
    public sealed class ClientTerrain : IDisposable
    {
        readonly Scene scene;
        readonly ChunkMeshFactory factory;
        readonly GameClient.TerrainState terrain;
        readonly SectionMeshQueue meshers;
        readonly Dictionary<SectionIndex, Entity> entities = new();
        readonly Queue<SectionIndex> dirtyQueue = new();
        readonly HashSet<SectionIndex> dirtySet = new();

        /// <summary>
        /// Submitted to a worker and not yet applied. Separate from <see cref="dirtySet"/> because a
        /// section can be re-dirtied while its job is running: the stale result still arrives, and it
        /// must not be treated as satisfying the newer request.
        /// </summary>
        readonly HashSet<SectionIndex> inFlight = new();

        readonly DirtySectionSink dirtySections;

        ChunkMap Map => terrain.Map;

        public ClientTerrain(Scene scene, ChunkMeshFactory factory, GameClient.TerrainState terrain)
        {
            this.scene = scene;
            this.factory = factory;
            this.terrain = terrain;
            meshers = new SectionMeshQueue(terrain.Map, SectionMeshQueue.DefaultWorkerCount);
            dirtySections = new DirtySectionSink(this);

            // A chunk becomes meshable when its last slab arrives — and so do its neighbours, whose
            // aprons read into it, which MarkChunkDirty already accounts for.
            terrain.ChunkCompleted += MarkChunkDirty;
        }

        public void Dispose() => meshers.Dispose();

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
        /// Main-thread time spent UPLOADING finished geometry per call. Meshing itself no longer happens
        /// here — it runs on <see cref="SectionMeshQueue"/>'s workers — so this budget now covers only GPU
        /// buffer creation, which is why a small number is no longer the bottleneck it was. A budget, not
        /// a hard cap: the check happens after each upload, so a call can overshoot by one section.
        /// </summary>
        const double UploadBudgetSeconds = 0.004;

        static readonly long UploadBudgetTicks = Math.Max(1, (long)(Stopwatch.Frequency * UploadBudgetSeconds));

        /// <summary>
        /// Sections a worker may be chewing on at once. Bounded for two reasons: finished geometry sits in
        /// memory until the main thread uploads it, and a section re-dirtied by an edit should not queue
        /// behind thousands of initial-load jobs.
        /// </summary>
        const int MaxInFlight = 64;

        /// <summary>
        /// How far down the dirty queue one call will look for something dispatchable. Blocked sections go
        /// to the back, so without a cap a large queue of not-yet-complete chunks — the outer ring of the
        /// loaded area — would be rescanned in full every frame.
        /// </summary>
        const int MaxDispatchScan = 256;

        /// <summary>
        /// Hands dirty sections to the mesher threads and uploads whatever came back. Call once per frame,
        /// not once per edit. Returns how many entities were swapped in.
        ///
        /// Sections whose 3x3 chunk neighbourhood hasn't fully arrived are moved to the back of the queue
        /// and retried — that gate is what makes off-thread meshing safe, and it also means the outer ring
        /// of the loaded area produces nothing until the ring beyond it exists.
        /// </summary>
        public int RebuildDirty()
        {
            Dispatch();
            return Collect();
        }

        void Dispatch()
        {
            int scans = Math.Min(dirtyQueue.Count, MaxDispatchScan);

            while (scans-- > 0 && inFlight.Count < MaxInFlight && dirtyQueue.Count > 0)
            {
                var section = dirtyQueue.Dequeue();

                if (!Map.Has(section.Chunk)) { dirtySet.Remove(section); continue; }

                // Already being meshed: leave it queued so the newer request is honoured after the
                // in-flight result lands, rather than racing two jobs for one section.
                // Not complete yet: a worker would read voxels the main thread is still decoding.
                if (inFlight.Contains(section) || !terrain.NeighbourhoodComplete(section.Chunk))
                {
                    dirtyQueue.Enqueue(section);
                    continue;
                }

                dirtySet.Remove(section);
                inFlight.Add(section);
                meshers.Submit(section);
            }
        }

        int Collect()
        {
            long start = Stopwatch.GetTimestamp();
            int rebuilt = 0;

            while (meshers.TryTakeResult(out var result))
            {
                // Not in flight means the world was reset under it — the section no longer exists as far
                // as we're concerned, so the geometry is garbage.
                if (!inFlight.Remove(result.Section)) continue;

                if (!result.Ready)
                {
                    EnqueueDirty(result.Section);   // apron chunk vanished between the gate and the fill
                    continue;
                }

                Swap(result.Section, result.Mesh);
                rebuilt++;

                if (Stopwatch.GetTimestamp() - start >= UploadBudgetTicks) break;
            }

            return rebuilt;
        }

        void Swap(SectionIndex section, MeshData mesh)
        {
            Entity? entity = factory.Build(section, mesh);

            // Detach the old entity BEFORE attaching the new one, or the stale geometry stays in
            // the scene and you get two overlapping surfaces after an edit.
            if (entities.Remove(section, out var previous)) previous.Scene = null;

            if (entity is null) return;

            entity.Scene = scene;
            entities[section] = entity;
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
