using System.Diagnostics;
using System.Numerics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// Terrain view: turns server-supplied voxels into section entities. Dirty marking is separate
    /// from <see cref="RebuildDirty"/> so same-frame edits coalesce into one remesh.
    /// </summary>
    public sealed class ClientTerrain : IDisposable
    {
        readonly Scene scene;
        readonly ChunkMeshFactory factory;
        readonly GameClient.IClientTerrainSource terrain;
        readonly SectionMeshQueue meshers;

        /// <summary>
            /// Cached mesh result, attached or detached. Null entities cache empty boxes so view changes
            /// do not remesh open air.
        /// </summary>
        struct Cached
        {
            public Entity? Entity;
            public SectionBuffers? Buffers;
            public bool Attached;

            /// <summary>Frame this box was last wanted, for LRU eviction.</summary>
            public long LastUsed;
        }

        /// <summary>Everything meshed and still believed current, on screen or not.</summary>
        readonly Dictionary<LodSection, Cached> cache = new();

        /// <summary>Cache entries holding GPU geometry — what the memory ceiling is actually about.</summary>
        int cachedGeometry;


        readonly List<(long LastUsed, LodSection Section)> evictionScratch = new();

        /// <summary>
        /// A scene removal is observed by Stride's render processors during the draw boundary. Keep
        /// the shared GPU buffers alive for a few complete frames after that removal, so an already
        /// prepared render node can never retain a freed binding.
        /// </summary>
        readonly Queue<(long ReleaseFrame, Entity Entity, SectionBuffers? Buffers)> pendingRelease = new();

        long frameCounter;

        // Reused per frame rather than allocated: this runs every frame forever.
        readonly List<(LodSection Section, MeshData Mesh)> batch = new();
        readonly List<Entity> built = new();
        int batchVertices;
        bool batchUrgent;
        bool uploadedBatch;
        /// <summary>
        /// Streaming work, NEAREST FIRST.
        ///
        /// It was a plain queue, so sections meshed in whatever order they were marked — which is
        /// the order the LOD selection walked the quadtree, not the order you can see them in. A box
        /// behind you could sit ahead of the ground under your feet, and on a busy frame the ground
        /// under your feet is what waits.
        ///
        /// Priority is squared distance from the eye to the box's centre, stamped when the section
        /// is queued and re-stamped whenever it is put back. Stale for a section that has waited
        /// while the player moved, and that is fine: this decides which of several thousand boxes to
        /// mesh next, and being approximately right about that is the whole of the job.
        /// </summary>
        readonly PriorityQueue<LodSection, float> dirtyQueue = new();

        /// <summary>Where distances are measured from. Updated with the LOD selection, since that is
        /// already the point at which the view is considered to have moved.</summary>
        Vector3 dispatchOrigin;

        /// <summary>
        /// Edit-dirtied sections, prioritized over the streaming backlog for visible response.
        /// </summary>
        readonly Queue<LodSection> urgentQueue = new();

        /// <summary>Set while an edit is being marked, so the sink knows which lane to use.</summary>
        bool markingUrgent;

        /// <summary>
        /// Edit mark and dispatch times used to measure swing-to-visible-hole latency.
        /// </summary>
        readonly Dictionary<LodSection, (long Marked, long Dispatched)> editTiming = new();
        readonly HashSet<LodSection> dirtySet = new();

        /// <summary>
        /// Worker jobs not yet applied. A section may become dirty again while in flight.
        /// </summary>
        readonly HashSet<LodSection> inFlight = new();

        /// <summary>Boxes the current player position says should exist. Recomputed when they move a chunk.</summary>
        readonly HashSet<LodSection> desired = new();

        /// <summary>Chunk the desired set was last computed for; null until the player exists.</summary>
        ChunkIndex? lodAnchor;

        /// <summary>View axis and lens the desired set was last computed for. Zero forward forces the
        /// first selection, since no real direction can be within 5 degrees of it.</summary>
        Vector3 lastForward;
        float lastTanHalfFov;

        /// <summary>Owns the refinement queue's buffers, so reselecting allocates nothing.</summary>
        readonly TerrainLod lod = new();

        /// <summary>
        /// Old-LOD boxes kept visible until their replacements upload, preventing transition holes.
        /// </summary>
        readonly Dictionary<LodSection, List<LodSection>> superseded = new();

        /// <summary>Scratch for retiring, since <see cref="superseded"/> cannot be mutated while walked.</summary>
        readonly List<LodSection> retired = new();

        /// <summary>
        /// Cache entries currently attached; avoids scanning detached history on reselection.
        /// </summary>
        readonly HashSet<LodSection> attached = new();

        /// <summary>
        /// Meshed boxes, including empty ones, so empty replacements can retire old geometry.
        /// </summary>
        readonly HashSet<LodSection> resolved = new();

        readonly DirtySectionSink dirtySections;
        readonly Action<Vector3, Vector3> onRegionEdited;

        ChunkMap Map => terrain.Map;

        public ClientTerrain(Scene scene, ChunkMeshFactory factory, GameClient.IClientTerrainSource terrain)
        {
            this.scene = scene;
            this.factory = factory;
            this.terrain = terrain;
            meshers = new SectionMeshQueue(terrain.Map, SectionMeshQueue.DefaultWorkerCount);
            dirtySections = new DirtySectionSink(this);

            // A chunk becomes meshable when its last slab arrives — and so do its neighbours, whose
            // aprons read into it, which MarkChunkDirty already accounts for.
            terrain.ChunkCompleted += MarkChunkDirty;

            // Edits already changed the field; coalesce same-frame dirty marks before remeshing.
            onRegionEdited = (min, max) =>
            {
                MarkRegionDirty(min, max);
            };
            terrain.RegionEdited += onRegionEdited;
        }

        public void Dispose()
        {
            terrain.ChunkCompleted -= MarkChunkDirty;
            terrain.RegionEdited -= onRegionEdited;
            meshers.Dispose();
            foreach (var (_, value) in cache)
            {
                value.Buffers?.Release();
                if (value.Entity is not null) value.Entity.Scene = null;
            }
            while (pendingRelease.TryDequeue(out var pending)) pending.Buffers?.Release();
            cache.Clear();
            attached.Clear();
            superseded.Clear();
            cachedGeometry = 0;
        }

        /// <summary>Marks every section of one chunk, and anything reading into it.</summary>
        public void MarkChunkDirty(ChunkIndex index)
        {
            (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);

            MarkRegionDirty(originX, ChunkConstants.WorldMinY, originZ,
                            originX + ChunkConstants.ChunkWidth - 1, ChunkConstants.WorldMaxY - 1,
                            originZ + ChunkConstants.ChunkWidth - 1);
        }

        public void MarkRegionDirty(Vector3 min, Vector3 max)
        {
            markingUrgent = true;
            MarkRegionDirty(
                (int)MathF.Floor(min.X), (int)MathF.Floor(min.Y), (int)MathF.Floor(min.Z),
                (int)MathF.Ceiling(max.X), (int)MathF.Ceiling(max.Y), (int)MathF.Ceiling(max.Z));
            markingUrgent = false;
        }

        /// <summary>
        /// Marks every section whose mesh depends on a changed world-voxel box. The geometry lives in
        /// <see cref="ChunkMesher.CollectDependentSections"/> so the server can reuse it.
        /// </summary>
        public void MarkRegionDirty(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
            => ChunkMesher.CollectDependentSections(minX, minY, minZ, maxX, maxY, maxZ, dirtySections);

        /// <summary>
        /// Bounds completed-but-not-uploaded memory and edit latency while keeping batches full.
        /// </summary>
        const int MaxInFlight = 96;

        /// <summary>
        /// Dirty entries inspected per call; prevents rescanning the incomplete outer ring every frame.
        /// </summary>
        const int MaxDispatchScan = 256;

        /// <summary>
        /// Sections per shared buffer pair. The whole point of batching — see
        /// <see cref="ChunkMeshFactory.UploadBatch"/> for why allocation COUNT is what matters.
        /// </summary>
        const int MaxBatchSections = 24;

        /// <summary>Also caps a batch, so one buffer stays a sane size when sections are dense.</summary>
        const int MaxBatchVertices = 60_000;

        /// <summary>
        /// Ordinary worker completions are staged to this many geometric sections before allocating
        /// Vulkan buffers. Workers otherwise trickle two or three results into each frame and pay the
        /// driver's fixed allocation cost for every tiny batch. Urgent edit meshes bypass the floor.
        ///
        /// REVERTED to 12 after raising it to 32 produced corrupted frames — visible garbage
        /// triangles over the sky, then an access violation in the mesh render feature. The
        /// mechanism was never established; what is established is that these caps were 64/150,000
        /// when the LOD landed and were deliberately reduced to 24/60,000 afterwards, and that
        /// raising them brought a rendering fault back. Treat that reduction as load-bearing until
        /// somebody explains it.
        ///
        /// The measurement that motivated the change still stands and is worth keeping: cost per
        /// batch is FLAT across a 2.3x spread in size (1,932 verts cost 5.18 ms, 4,529 cost 5.15),
        /// so the price is the fixed allocation and fewer-fuller batches genuinely would be cheaper.
        /// The way to get there is fewer sections needing upload at all — which the cache fix
        /// delivers — not a bigger cap.
        /// </summary>
        const int MinBatchSections = 12;

        /// <summary>At most one render-node insertion batch per frame — one fixed allocation cost is
        /// what a frame can afford. At the raised floor that is still nearly two thousand sections a
        /// second at 60 FPS.</summary>
        const int MaxUploadBatchesPerFrame = 1;

        /// <summary>Render-node removals per frame. A cache trim may have hundreds of candidates;
        /// spreading it out prevents one view turn from rebuilding the render feature's node arrays.</summary>
        const int MaxEvictionsPerFrame = 24;

        /// <summary>Frames between removing an entity and releasing its shared GPU bindings.</summary>
        const int BufferReleaseDelayFrames = 3;

        /// <summary>
        /// How much GPU-backed geometry may be held BEYOND what is currently on screen.
        ///
        /// Expressed as headroom over the live set rather than as a flat ceiling, and that is a fix
        /// rather than a preference. A flat 1024 never bounded anything: eviction may only take boxes
        /// that are detached and unwanted, so live geometry is untouchable by it. With 1273 boxes
        /// live the budget was already blown before a single cached box existed, so the check fired
        /// every frame and the only thing it could evict was the cache itself. Measured result:
        /// `reused 0` against `evicted 604` — a cache with a zero percent hit rate, which is to say
        /// no cache, which is to say every box that left the view and came back was re-meshed.
        ///
        /// Headroom cannot invert that way. It is always room for a cache on top of whatever is
        /// being drawn, and it grows and shrinks with the view rather than with the constant.
        /// </summary>
        const int CachedGeometryHeadroom = 1536;

        /// <summary>Floor for the above, so a nearly empty view still keeps a usable cache.</summary>
        const int MinCachedGeometry = 1024;

        /// <summary>
        /// Total cache entries, empties included. Empties cost a dictionary slot rather than memory,
        /// but an unbounded set that grows with distance walked is a leak whatever each entry costs.
        /// </summary>
        const int MaxCachedSections = 16_384;

        /// <summary>Evict down to this fraction of a ceiling, so eviction is occasional rather than
        /// once per frame at the boundary.</summary>
        const float EvictionTarget = 0.9f;

        /// <summary>
        /// View rotation that triggers LOD reselection. The widened selection frustum exceeds this
        /// threshold, avoiding visible edge flicker.
        /// </summary>
        const float ReselectDegrees = 5f;

        static readonly float ReselectCosine = MathF.Cos(ReselectDegrees * MathF.PI / 180f);

        /// <summary>Relative change in the lens that forces a reselect. Small, because this is what
        /// makes aiming refine at all, and the ADS blend is a lerp rather than a step.</summary>
        const float ReselectFovFraction = 0.02f;

        /// <summary>
        /// Whether terrain around a position has resolved, including boxes that mesh to empty.
        /// </summary>
        public bool IsMeshedAround(Vector3 position, float radius)
        {
            int minX = (int)MathF.Floor(position.X - radius);
            int maxX = (int)MathF.Floor(position.X + radius);
            int minZ = (int)MathF.Floor(position.Z - radius);
            int maxZ = (int)MathF.Floor(position.Z + radius);

            foreach (var box in desired)
            {
                if (box.OriginX + box.Size <= minX || box.OriginX > maxX) continue;
                if (box.OriginZ + box.Size <= minZ || box.OriginZ > maxZ) continue;
                if (!resolved.Contains(box)) return false;
            }

            // Nothing desired here at all means the LOD set has not been built yet.
            return desired.Count > 0;
        }

        /// <summary>
        /// Dispatches dirty sections and uploads completed meshes once per frame. Sections wait until
        /// their 3x3 chunk footprint has arrived. Returns the number of swapped entities.
        /// </summary>
        public int RebuildDirty(in TerrainView view)
        {
            frameCounter++;
            RefreshLod(view);
            Dispatch();
            return Collect();
        }

        /// <summary>
        /// Recomputes desired LOD boxes after meaningful position, view, or lens changes.
        /// </summary>
        void RefreshLod(in TerrainView view)
        {
            var anchor = ChunkTransforms.ChunkAt(view.Origin);

            bool moved = lodAnchor is not { } previous || !previous.Equals(anchor);
            bool turned = Vector3.Dot(view.Forward, lastForward) < ReselectCosine;
            bool zoomed = MathF.Abs(view.TanHalfFovY - lastTanHalfFov)
                > ReselectFovFraction * MathF.Max(lastTanHalfFov, 1e-4f);

            if (!moved && !turned && !zoomed) return;

            long refreshStart = Stopwatch.GetTimestamp();

            lodAnchor = anchor;
            dispatchOrigin = view.Origin;
            lastForward = view.Forward;
            lastTanHalfFov = view.TanHalfFovY;
            lod.CollectDesired(view, desired);

            // Keep resolved boxes outside the desired set as the LOD cache. Edits invalidate every
            // cached level they touch. Test resolution, not geometry: most boxes are empty.
            foreach (var wanted in desired)
            {
                Touch(wanted);

                if (!resolved.Contains(wanted) && !inFlight.Contains(wanted)) EnqueueDirty(wanted);
            }

            // Keep old-LOD geometry until every replacement box resolves.
            foreach (var live in attached)
            {
                if (desired.Contains(live)) continue;
                if (superseded.ContainsKey(live)) continue;

                var replacements = new List<LodSection>();
                CollectReplacements(live, replacements);
                superseded[live] = replacements;
            }

            stats.LodRefresh(Stopwatch.GetTimestamp() - refreshStart);
        }

        void Dispatch()
        {
            // Scan the small edit lane fully so an in-flight section cannot block ready neighbours.
            int urgentScans = urgentQueue.Count;
            while (urgentScans-- > 0 && inFlight.Count < MaxInFlight && urgentQueue.Count > 0)
            {
                var edit = urgentQueue.Dequeue();
                if (!TryDispatch(edit, urgentLane: true)) urgentQueue.Enqueue(edit);
            }

            int scans = Math.Min(dirtyQueue.Count, MaxDispatchScan);

            while (scans-- > 0 && inFlight.Count < MaxInFlight && dirtyQueue.Count > 0)
            {
                var next = dirtyQueue.Dequeue();

                // Re-stamped on the way back in rather than keeping its old key: a section that could
                // not go yet has been waiting, and where the player is NOW is what should decide when
                // it is tried again.
                if (!TryDispatch(next, urgentLane: false))
                    dirtyQueue.Enqueue(next, DistanceSqFromEye(next));
            }
        }

        /// <summary>Squared distance from the eye to a box's centre. Squared because it is only ever
        /// compared, and the box's centre because its corner would rank a big distant box by whichever
        /// corner happened to face you.</summary>
        float DistanceSqFromEye(LodSection section)
        {
            float half = section.Size * 0.5f;
            float dx = section.OriginX + half - dispatchOrigin.X;
            float dy = section.OriginY + half - dispatchOrigin.Y;
            float dz = section.OriginZ + half - dispatchOrigin.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// Submits one section. Returns FALSE when it cannot go yet and the caller should put it
        /// back — the caller rather than this, because the two lanes requeue differently: edits keep
        /// their arrival order, streaming work is re-ranked by distance on the way back in.
        /// </summary>
        bool TryDispatch(LodSection section, bool urgentLane)
        {
            // No longer wanted at this level — the player moved and it was replaced by a coarser or
            // finer box, so meshing it would be work nobody will look at.
            // A dropped section never reaches ReportEditLatency, so its timing entry has to go here
            // or editTiming grows for the life of the session.
            if (!desired.Contains(section))
            {
                dirtySet.Remove(section);
                editTiming.Remove(section);
                return true;
            }

            // Already being meshed: leave it queued so the newer request is honoured after the
            // in-flight result lands, rather than racing two jobs for one section.
            if (inFlight.Contains(section)) return false;

            // Workers cannot read incomplete footprints. Drop incomplete edit jobs; ChunkCompleted
            // marks them again if they later stream in. Ordinary streaming jobs wait in their queue.
            if (!terrain.FootprintComplete(section))
            {
                if (urgentLane)
                {
                    dirtySet.Remove(section);
                    editTiming.Remove(section);
                    return true;
                }
                return false;
            }

            // An edit-dirtied section is urgent all the way through, not just to the front of the
            // submit queue: its result is taken before the streaming backlog too.
            bool urgent = editTiming.TryGetValue(section, out var timing);
            if (urgent && timing.Dispatched == 0L)
                editTiming[section] = (timing.Marked, Stopwatch.GetTimestamp());

            dirtySet.Remove(section);
            inFlight.Add(section);
            resolved.Remove(section);      // being re-meshed: not settled until it comes back
            meshers.Submit(section, urgent);
            return true;
        }

        int Collect()
        {
            ReleaseRetiredBuffers();
            int applied = 0;
            int uploadBatches = 0;

            // The explicit batch cap is the frame budget: Vulkan allocation cannot be interrupted
            // part-way through, so a time check after it would only describe an overshoot.
            while (true)
            {
                uploadedBatch = false;
                applied += CollectOneBatch();

                if (!uploadedBatch) break;
                if (++uploadBatches >= MaxUploadBatchesPerFrame) break;
            }

            RetireCovered();
            EvictOverBudget();

            stats.EndFrame(
                dirtyQueue.Count,
                inFlight.Count,
                urgentQueue.Count,
                urgentQueue.Count > 0 && inFlight.Count >= MaxInFlight,
                new Residency(
                    Live: attached.Count,
                    Desired: desired.Count,
                    Superseded: superseded.Count,
                    Resolved: resolved.Count,
                    CachedGeometry: cachedGeometry,
                    CachedTotal: cache.Count,
                    HitCeiling: lod.LastHitCeiling));
            return applied;
        }

        /// <summary>
        /// Detaches superseded geometry once every box that covers it is on screen. Until then the old and
        /// new overlap, which draws that patch twice for a moment — far cheaper than a hole, and brief.
        /// </summary>
        void RetireCovered()
        {
            retired.Clear();

            foreach (var (old, replacements) in superseded)
            {
                // Wanted again before its replacements ever landed — the view swung back. Cancel the
                // retirement outright rather than hiding a box that is currently desired, which is a
                // hole the cache would otherwise open every time somebody looked away and back.
                if (desired.Contains(old))
                {
                    retired.Add(old);
                    continue;
                }

                foreach (var replacement in replacements)
                    if (!resolved.Contains(replacement)) goto next;

                Hide(old);
                retired.Add(old);

                next: ;
            }

            foreach (var old in retired) superseded.Remove(old);
        }

        /// <summary>
        /// The desired boxes covering the same world as one that is on screen at the wrong level.
        ///
        /// Found by SHIFTING rather than by scanning the desired set, and that is not a tidiness.
        /// Levels nest exactly — every box is an aligned octree cell — so a box's coarser counterpart
        /// is one shift away and its finer ones are a contiguous block, at most 72 lookups all told.
        /// Scanning instead costs one pass over the whole desired set PER superseded box, which was
        /// affordable when reselection only happened on a chunk boundary and is quadratic now that it
        /// happens whenever the player turns five degrees.
        /// </summary>
        void CollectReplacements(LodSection live, List<LodSection> into)
        {
            for (int level = live.Level + 1; level <= LodSection.MaxLevel; level++)
            {
                int shift = level - live.Level;
                var box = new LodSection(live.X >> shift, live.Y >> shift, live.Z >> shift, level);

                if (desired.Contains(box)) into.Add(box);
            }

            for (int level = live.Level - 1; level >= 0; level--)
            {
                int shift = live.Level - level;
                int span = 1 << shift;
                int baseX = live.X << shift, baseY = live.Y << shift, baseZ = live.Z << shift;

                for (int dx = 0; dx < span; dx++)
                    for (int dy = 0; dy < span; dy++)
                        for (int dz = 0; dz < span; dz++)
                        {
                            var box = new LodSection(baseX + dx, baseY + dy, baseZ + dz, level);

                            if (desired.Contains(box)) into.Add(box);
                        }
            }
        }

        /// <summary>
        /// Drains finished results into one shared buffer pair. Empty meshes are applied immediately and
        /// never reach the batch — they cost nothing and would only dilute it.
        /// </summary>
        int CollectOneBatch()
        {
            built.Clear();

            int applied = 0;
            if (!batchUrgent)
                foreach (var pending in batch)
                    if (editTiming.ContainsKey(pending.Section))
                    {
                        batchUrgent = true;
                        break;
                    }

            while (batch.Count < MaxBatchSections
                   && batchVertices < MaxBatchVertices
                   && meshers.TryTakeResult(out var result))
            {
                // Not in flight means the world was reset under it — the section no longer exists as far
                // as we're concerned, so the geometry is garbage.
                if (!inFlight.Remove(result.Section)) continue;

                if (!result.Ready)
                {
                    stats.NotReady();
                    EnqueueDirty(result.Section);   // apron chunk vanished between the gate and the fill
                    continue;
                }

                if (result.Mesh.Indices.Length == 0)
                {
                    // Meshed to nothing. Cached as an EMPTY rather than merely forgotten, so a later
                    // reselection knows the answer instead of asking again — see Cached's comment.
                    Attach(result.Section, entity: null, buffers: null, wanted: true);
                    resolved.Add(result.Section);
                    ReportEditLatency(result.Section);
                    stats.Record(hasGeometry: false, 0, 0);
                    applied++;
                    continue;
                }

                batch.Add((result.Section, result.Mesh));
                batchVertices += result.Mesh.Positions.Length;
                batchUrgent |= editTiming.ContainsKey(result.Section);
            }

            if (batch.Count == 0) return applied;

            // A drained worker pool will not make this partial batch any fuller. Edits are latency
            // sensitive and bypass staging; streaming geometry waits for enough neighbours to make
            // the fixed Vulkan allocation worthwhile.
            if (!batchUrgent
                && batch.Count < MinBatchSections
                && inFlight.Count > 0)
                return applied;

            long before = Stopwatch.GetTimestamp();
            var buffers = factory.UploadBatch(batch, built);
            long cost = Stopwatch.GetTimestamp() - before;

            for (int i = 0; i < batch.Count; i++)
            {
                // Not desired any more means the view moved while this was in flight. Keep the mesh —
                // that is what the cache is for — but do not put it on screen at a level nobody asked
                // for, or it would overlap whatever replaced it.
                Attach(batch[i].Section, built[i], buffers, wanted: desired.Contains(batch[i].Section));
                resolved.Add(batch[i].Section);
                ReportEditLatency(batch[i].Section);
            }

            stats.Record(hasGeometry: true, cost, factory.LastGpuTicks, batch.Count, batchVertices);
            applied += batch.Count;
            batch.Clear();
            batchVertices = 0;
            batchUrgent = false;
            uploadedBatch = true;
            return applied;
        }

        /// <summary>
        /// Splits an edit's visible delay into the two things that can be slow about it: waiting for
        /// a worker slot, and everything after — meshing, then queueing behind other finished
        /// meshes for a share of the frame's upload budget.
        /// </summary>
        void ReportEditLatency(LodSection section)
        {
            if (!editTiming.Remove(section, out var timing)) return;
            long now = Stopwatch.GetTimestamp();
            long dispatched = timing.Dispatched == 0L ? now : timing.Dispatched;
            stats.EditVisible(dispatched - timing.Marked, now - dispatched);
        }

        /// <summary>Marks a box as wanted now, and puts it back on screen if it is already meshed.</summary>
        void Touch(LodSection section)
        {
            if (!cache.TryGetValue(section, out var entry)) return;

            entry.LastUsed = frameCounter;

            if (!entry.Attached && entry.Entity is not null)
            {
                entry.Entity.Get<ModelComponent>()!.Enabled = true;
                entry.Attached = true;
                attached.Add(section);
                stats.Reused();
            }

            cache[section] = entry;
        }

        /// <summary>Takes a box off screen but KEEPS its geometry. The cheap half of the cache.</summary>
        void Hide(LodSection section)
        {
            if (!cache.TryGetValue(section, out var entry) || !entry.Attached) return;

            if (entry.Entity is not null)
                entry.Entity.Get<ModelComponent>()!.Enabled = false;

            entry.Attached = false;
            attached.Remove(section);
            cache[section] = entry;
        }

        /// <summary>Drops a box entirely, releasing its claim on the shared buffers.</summary>
        void Evict(LodSection section)
        {
            if (!cache.Remove(section, out var entry)) return;

            if (entry.Entity is not null)
            {
                entry.Entity.Scene = null;
                cachedGeometry--;
                pendingRelease.Enqueue((
                    frameCounter + BufferReleaseDelayFrames,
                    entry.Entity,
                    entry.Buffers));
            }

            attached.Remove(section);
            if (entry.Entity is null) entry.Buffers?.Release();
            resolved.Remove(section);
        }

        void ReleaseRetiredBuffers()
        {
            while (pendingRelease.TryPeek(out var pending)
                   && pending.ReleaseFrame <= frameCounter)
            {
                pendingRelease.Dequeue();
                pending.Buffers?.Release();
            }
        }

        /// <summary>
        /// An edit landed inside a box that is cached at a level nobody is currently asking for.
        ///
        /// Dropping it is the whole invalidation story, and it is cheap in the right way: edits are
        /// rare next to view changes, and the cost is one re-mesh IF the player ever looks at that
        /// level again. The alternative — keeping it and re-meshing on return — would need the dirty
        /// mark to survive arbitrarily long in a set nobody bounds.
        /// </summary>
        void Invalidate(LodSection section)
        {
            if (!cache.ContainsKey(section)) return;

            Evict(section);
            stats.Invalidated();
        }

        void Attach(LodSection section, Entity? entity, SectionBuffers? buffers, bool wanted)
        {
            // Drop the old BEFORE inserting the new one, or the stale geometry stays in the scene and
            // you get two overlapping surfaces after an edit.
            Evict(section);

            if (entity is not null)
            {
                cachedGeometry++;
                entity.Get<ModelComponent>()!.Enabled = wanted;
                entity.Scene = scene;
                if (wanted) attached.Add(section);
            }

            cache[section] = new Cached
            {
                Entity = entity,
                Buffers = buffers,
                Attached = wanted && entity is not null,
                LastUsed = frameCounter,
            };
        }

        /// <summary>
        /// Drops the least recently wanted cached boxes once either ceiling is passed.
        ///
        /// Only DETACHED, undesired boxes are candidates: evicting something on screen would open a
        /// hole, and evicting something desired would immediately re-mesh it. The sort is over the
        /// candidates rather than the whole cache and only runs when a ceiling is actually exceeded.
        /// </summary>
        void EvictOverBudget()
        {
            // The live set is not evictable, so the budget has to be measured from it — see
            // CachedGeometryHeadroom for what happens when it is not.
            int geometryCeiling = Math.Max(MinCachedGeometry, attached.Count + CachedGeometryHeadroom);
            if (cachedGeometry <= geometryCeiling && cache.Count <= MaxCachedSections) return;

            evictionScratch.Clear();
            foreach (var (section, entry) in cache)
                if (!entry.Attached && !desired.Contains(section))
                    evictionScratch.Add((entry.LastUsed, section));

            evictionScratch.Sort(static (a, b) => a.LastUsed.CompareTo(b.LastUsed));

            int geometryTarget = (int)(geometryCeiling * EvictionTarget);
            int sectionTarget = (int)(MaxCachedSections * EvictionTarget);

            int evictions = 0;
            foreach (var (_, section) in evictionScratch)
            {
                if (cachedGeometry <= geometryTarget && cache.Count <= sectionTarget) break;
                if (evictions++ >= MaxEvictionsPerFrame) break;
                Evict(section);
                stats.Evicted();
            }
        }

        // ---- Diagnostics ----

        Diagnostics stats;

        /// <summary>
        /// What the LOD bookkeeping is currently holding. Reported because the counts that matter are
        /// the ones that should be BOUNDED by how far you can see, not by how far you have walked: live
        /// entities, boxes waiting to be retired, and the resolved set. A number here that only ever
        /// climbs is a leak, and a leak here costs a scene entity, a draw, and a share of a GPU buffer
        /// that cannot be freed until the last section using it is detached.
        /// </summary>
        readonly record struct Residency(
            int Live,
            int Desired,
            int Superseded,
            int Resolved,
            int CachedGeometry,
            int CachedTotal,
            bool HitCeiling);

        /// <summary>
        /// Where terrain load time actually goes. Kept in the build rather than bolted on when needed:
        /// this pipeline has had its bottleneck mis-identified three times by reasoning about it, and
        /// every one of those would have been settled in a minute by these six numbers.
        ///
        /// Logs once a second while there is work outstanding, then goes quiet.
        /// </summary>
        struct Diagnostics
        {
            static readonly Stride.Core.Diagnostics.Logger Log =
                Stride.Core.Diagnostics.GlobalLogger.GetLogger("Terrain");

            long windowStart;
            int frames;
            int uploads;             // sections that produced geometry and so cost a GPU buffer

            /// <summary>
            /// Vertices per batch, reported so the batching question can be settled by measurement
            /// rather than argument: if the upload cost tracks this, it is proportional to bytes and
            /// bigger batches buy nothing; if it tracks the batch COUNT instead, the cost is the
            /// fixed Vulkan allocation and batches should be fewer and fuller.
            /// </summary>
            long batchVerts;
            int empties;         // sections that meshed to nothing; effectively free
            long uploadTicks;    // whole Swap: CPU prep + GPU buffers + scene attach
            long gpuTicks;       // just the two Buffer.New calls inside it
            long emptyTicks;
            int budgetHits;      // frames where the upload budget cut collection short
            int urgentBlocked;   // frames where an edit had sections waiting and the pool was full
            int notReady;        // meshes that came back unusable and went straight back on the queue

            int batches;

            public void Record(bool hasGeometry, long ticks, long gpu, int sections = 1, long vertices = 0)
            {
                if (hasGeometry) { uploads += sections; batches++; uploadTicks += ticks; gpuTicks += gpu; batchVerts += vertices; }
                else { empties += sections; emptyTicks += ticks; }
            }

            public void BudgetHit() => budgetHits++;

            public void NotReady() => notReady++;

            // The cache window: boxes put back on screen without meshing, boxes dropped for space, and
            // boxes dropped because an edit landed in a level nobody is currently looking at.
            int reused;
            int evicted;
            int invalidated;

            public void Reused() => reused++;

            public void Evicted() => evicted++;

            public void Invalidated() => invalidated++;

            // Edit-to-visible, split at the moment a worker picked the section up.
            int editSections;
            long editWaitTicks;     // marked -> dispatched
            long editWorkTicks;     // dispatched -> on screen
            long editWorstTicks;    // worst total for one section this window

            public void EditVisible(long waitTicks, long workTicks)
            {
                editSections++;
                editWaitTicks += waitTicks;
                editWorkTicks += workTicks;
                editWorstTicks = Math.Max(editWorstTicks, waitTicks + workTicks);
            }

            // Chunk-boundary LOD reconciliation, which is the one part of this pipeline whose cost is
            // superlinear in what it is holding: it walks live entities against the superseded list and
            // the desired set. A hitch that grows as you walk shows up here first.
            int lodRefreshes;
            long lodRefreshTicks;
            long lodRefreshWorstTicks;

            public void LodRefresh(long ticks)
            {
                lodRefreshes++;
                lodRefreshTicks += ticks;
                lodRefreshWorstTicks = Math.Max(lodRefreshWorstTicks, ticks);
            }

            public void EndFrame(
                int dirtyDepth, int inFlightCount, int urgentDepth, bool urgentStarved, Residency residency)
            {
                frames++;
                if (urgentStarved) urgentBlocked++;

                long now = Stopwatch.GetTimestamp();
                if (windowStart == 0) windowStart = now;

                double elapsed = (now - windowStart) / (double)Stopwatch.Frequency;
                if (elapsed < 1.0) return;

                double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;

                // Residency is reported even when the pipeline is idle, because the failure it exists to
                // catch — counts that grow with distance walked rather than with view distance — is
                // invisible in a quiet window by definition.
                if (residency.Superseded > 0 || lodRefreshes > 0 || dirtyDepth > 0 || inFlightCount > 0)
                    Log.Info(
                        $"terrain lod: live {residency.Live} | desired {residency.Desired} "
                      + $"| awaiting retire {residency.Superseded} | resolved {residency.Resolved} "
                      + $"| refresh {lodRefreshes}x avg {Ms(lodRefreshTicks) / Math.Max(lodRefreshes, 1):F2} ms "
                      + $"worst {Ms(lodRefreshWorstTicks):F2} ms"
                      + (residency.HitCeiling ? " | CEILING" : ""));

                // The cache's own numbers. `reused` is the payoff — boxes put back on screen without
                // meshing — and if it is not comfortably larger than `sections` while aiming around,
                // the cache is not earning the memory it holds. Quiet when nothing moved, like the
                // block above, but the RESIDENT counts are printed whenever anything else is, because
                // a cached count that only climbs is a leak and a leak is invisible in a still frame.
                if (reused + evicted + invalidated > 0 || lodRefreshes > 0)
                    Log.Info(
                        $"terrain cache: {residency.CachedGeometry} geometry + "
                      + $"{residency.CachedTotal - residency.CachedGeometry} empty "
                      + $"= {residency.CachedTotal} | reused {reused} | evicted {evicted} "
                      + $"| invalidated {invalidated}");

                // Nothing outstanding: stay quiet rather than logging zeroes forever.
                if (uploads + empties > 0 || dirtyDepth > 0 || inFlightCount > 0)
                {
                    Log.Info(
                        $"terrain: {frames} frames | {uploads} sections in {batches} batches "
                      + $"@ {Ms(uploadTicks) / Math.Max(batches, 1):F2} ms/batch "
                      + $"(gpu {Ms(gpuTicks) / Math.Max(batches, 1):F2}, "
                      + $"{batchVerts / Math.Max(batches, 1):N0} verts) = {uploads / elapsed:F0} sections/s "
                      + $"| empty {empties} | budget cut {budgetHits}/{frames} "
                      + $"| dirty {dirtyDepth} | inFlight {inFlightCount} "
                      + $"| urgent {urgentDepth} blocked {urgentBlocked}/{frames} | notReady {notReady}");

                    if (editSections > 0)
                        Log.Info(
                            $"terrain edits: {editSections} sections visible | "
                          + $"wait {Ms(editWaitTicks) / editSections:F1} ms + "
                          + $"mesh/upload {Ms(editWorkTicks) / editSections:F1} ms = "
                          + $"{Ms(editWaitTicks + editWorkTicks) / editSections:F1} ms avg, "
                          + $"worst {Ms(editWorstTicks):F1} ms");
                }

                windowStart = now;
                reused = evicted = invalidated = 0;
                frames = uploads = empties = budgetHits = batches = urgentBlocked = notReady = 0;
                batchVerts = 0;
                editSections = 0;
                editWaitTicks = editWorkTicks = editWorstTicks = 0;
                uploadTicks = emptyTicks = gpuTicks = 0;
                lodRefreshes = 0;
                lodRefreshTicks = lodRefreshWorstTicks = 0;
            }
        }

        void EnqueueDirty(LodSection section)
        {
            // The desired set already excludes anything outside the meshable region and anything at the
            // wrong level for where the player is, so it is the only membership test needed.
            if (!desired.Contains(section)) return;

            // An already-queued section that an edit now touches is PROMOTED: it is in dirtySet, so
            // the ordinary path would drop this call and leave the dig waiting in the slow lane.
            if (!dirtySet.Add(section) && !markingUrgent) return;

            // Re-stamped on EVERY edit, not just the first. A player digs the same hole repeatedly,
            // so keeping the original timestamp measured from a dig two digs ago and reported a
            // latency nobody experienced — it made a responsive pipeline look half a second slow.
            // What is being measured is "how long until I see the edit I just made".
            if (markingUrgent)
                editTiming[section] = (Stopwatch.GetTimestamp(), 0L);

            if (markingUrgent) urgentQueue.Enqueue(section);
            else dirtyQueue.Enqueue(section, DistanceSqFromEye(section));
        }

        /// <summary>
        /// Adapts <see cref="ChunkMesher.CollectDependentSections"/>, which speaks LOD 0, to whatever box
        /// currently covers that part of the world. Coarser levels are found by shifting, which floors
        /// correctly for negative coordinates.
        /// Only Add is ever called; the rest of ICollection exists to satisfy the signature.
        ///
        /// EVERY LEVEL IS VISITED, not just the one currently desired, and that is the cache's
        /// correctness condition rather than a tidiness. Exactly one level of a given voxel is desired
        /// — the levels partition the world — so that one is re-meshed as before. The other two may
        /// hold CACHED geometry that predates this edit, and the cache is precisely a promise to show
        /// that geometry again without re-meshing it. Dropping it here is what makes the promise safe.
        /// Before the cache existed, RefreshLod's `resolved.IntersectWith(desired)` covered this by
        /// distrusting anything that had ever left the desired set; deleting that line is what moved
        /// the obligation to this loop.
        /// </summary>
        sealed class DirtySectionSink(ClientTerrain owner) : ICollection<SectionIndex>
        {
            public int Count => owner.dirtySet.Count;
            public bool IsReadOnly => false;

            public void Add(SectionIndex item)
            {
                for (int level = 0; level <= LodSection.MaxLevel; level++)
                {
                    var box = new LodSection(item.x >> level, item.y >> level, item.z >> level, level);

                    if (owner.desired.Contains(box)) owner.EnqueueDirty(box);
                    else owner.Invalidate(box);
                }
            }

            public void Clear()
            {
                owner.dirtyQueue.Clear();
                owner.dirtySet.Clear();
            }

            public bool Contains(SectionIndex item) => false;
            public void CopyTo(SectionIndex[] array, int arrayIndex) { }
            public bool Remove(SectionIndex item) => throw new NotSupportedException();
            public IEnumerator<SectionIndex> GetEnumerator() => Enumerable.Empty<SectionIndex>().GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
