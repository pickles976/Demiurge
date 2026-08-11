using System.Diagnostics;
using System.Numerics;
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
        readonly GameClient.IClientTerrainSource terrain;
        readonly SectionMeshQueue meshers;

        /// <summary>
        /// One box's meshing result, kept whether or not it is currently on screen.
        ///
        /// The cache is the reason a level change is no longer a re-mesh. Selection changes with the
        /// VIEW now, not only with the player's position, so aiming across a valley and lowering the
        /// rifle again used to mean meshing the same few hundred boxes twice a second; keeping the
        /// built entity and toggling <c>Entity.Scene</c> makes the second aim free. Detaching is the
        /// cheapest operation Stride offers here and it releases nothing, which is exactly what is
        /// wanted.
        ///
        /// <see cref="Entity"/> is null when the box meshed to NOTHING, and that case has to be
        /// cached too rather than merely forgotten: three quarters of all boxes are open air, so a
        /// cache that only remembered geometry would re-mesh the empty majority of the world on every
        /// view change — the same mistake, one layer along, that `resolved` exists to prevent.
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

        long frameCounter;

        // Reused per frame rather than allocated: this runs every frame forever.
        readonly List<(LodSection Section, MeshData Mesh)> batch = new();
        readonly List<Entity> built = new();
        readonly Queue<LodSection> dirtyQueue = new();

        /// <summary>
        /// Sections dirtied by an EDIT rather than by streaming, dispatched ahead of the ordinary
        /// queue.
        ///
        /// Not a micro-optimisation: the ordinary queue holds the whole world as it loads, so a dig
        /// with no lane of its own waits behind thousands of sections nobody is looking at. The
        /// player is standing over the hole watching nothing happen. There is no throughput argument
        /// here at all — the same work gets done either way — it is purely about which of two
        /// equally cheap jobs the player is actually waiting on.
        /// </summary>
        readonly Queue<LodSection> urgentQueue = new();

        /// <summary>Set while an edit is being marked, so the sink knows which lane to use.</summary>
        bool markingUrgent;

        /// <summary>
        /// When each edit-dirtied section entered the urgent lane, and when it was handed to a
        /// worker. Kept only for sections an EDIT touched, so it holds a handful of entries rather
        /// than the streaming backlog — this measures the one latency a player actually feels,
        /// between swinging at the ground and seeing the hole.
        /// </summary>
        readonly Dictionary<LodSection, (long Marked, long Dispatched)> editTiming = new();
        readonly HashSet<LodSection> dirtySet = new();

        /// <summary>
        /// Submitted to a worker and not yet applied. Separate from <see cref="dirtySet"/> because a
        /// section can be re-dirtied while its job is running: the stale result still arrives, and it
        /// must not be treated as satisfying the newer request.
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
        /// Boxes at a level we no longer want, kept ON SCREEN until the boxes that replace them have
        /// actually been uploaded. Detaching on the spot leaves a hole in the terrain for as long as
        /// meshing and uploading take, which is very visible when walking across a level boundary.
        /// </summary>
        readonly Dictionary<LodSection, List<LodSection>> superseded = new();

        /// <summary>Scratch for retiring, since <see cref="superseded"/> cannot be mutated while walked.</summary>
        readonly List<LodSection> retired = new();

        /// <summary>
        /// Cache entries currently in the scene. Maintained rather than derived because reselection
        /// walks it, and reselection now happens when the player TURNS — walking the whole cache, most
        /// of which is detached, would make turning cost more the longer the session ran.
        /// </summary>
        readonly HashSet<LodSection> attached = new();

        /// <summary>
        /// Boxes that have been meshed, whether or not they produced geometry. Distinct from
        /// <see cref="entities"/> and the distinction matters: most boxes mesh to nothing, so waiting for
        /// an ENTITY to appear would keep superseded geometry on screen forever wherever the replacement
        /// turned out to be empty air.
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

            // Edits arrive already applied to the field; all that is left is to re-mesh what moved.
            // Marking is separate from rebuilding on purpose, so several digs landing in one frame
            // collapse into one re-mesh of the section they share — which is the normal case, since
            // a player digs the same hole repeatedly.
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
        /// Main-thread time spent uploading finished geometry per call — GPU buffer creation, since
        /// meshing runs on <see cref="SectionMeshQueue"/>'s workers.
        ///
        /// Raised from 4 ms once batching made an upload cheap. At 4 ms with per-section buffers costing
        /// 11 ms, this admitted exactly ONE section per frame, which is what made a world take half a
        /// minute. A budget, not a hard cap: the check is between batches, so a call overshoots by one.
        /// </summary>
        const double UploadBudgetSeconds = 0.012;

        static readonly long UploadBudgetTicks = Math.Max(1, (long)(Stopwatch.Frequency * UploadBudgetSeconds));

        /// <summary>
        /// Sections a worker may be chewing on at once. Bounded for two reasons: finished geometry sits in
        /// memory until the main thread uploads it, and a section re-dirtied by an edit should not queue
        /// behind thousands of initial-load jobs. Raised alongside batching so a batch has enough
        /// finished work to be worth one allocation.
        /// </summary>
        const int MaxInFlight = 192;

        /// <summary>
        /// How far down the dirty queue one call will look for something dispatchable. Blocked sections go
        /// to the back, so without a cap a large queue of not-yet-complete chunks — the outer ring of the
        /// loaded area — would be rescanned in full every frame.
        /// </summary>
        const int MaxDispatchScan = 256;

        /// <summary>
        /// Sections per shared buffer pair. The whole point of batching — see
        /// <see cref="ChunkMeshFactory.UploadBatch"/> for why allocation COUNT is what matters.
        /// </summary>
        const int MaxBatchSections = 64;

        /// <summary>Also caps a batch, so one buffer stays a sane size when sections are dense.</summary>
        const int MaxBatchVertices = 150_000;

        /// <summary>
        /// Cached sections holding GPU geometry before the oldest detached ones are dropped.
        ///
        /// This is the cache's real cost: on the reference machine the iGPU and the CPU share one
        /// DDR4 bus, so a buffer held for a view the player might return to competes for bandwidth
        /// with the frame that is actually being drawn. Roughly twice the live count observed on the
        /// conquest map, which is enough to hold a couple of aim directions plus the walk between
        /// them.
        ///
        /// Note the ceiling counts SECTIONS, not buffers, and a batch's buffer pair survives until its
        /// last section is released — so one cached section can pin up to 63 others' memory. Whether
        /// that matters is a measurement, not a guess: <see cref="Diagnostics"/> reports live buffer
        /// bytes so the pinning shows up if it is real.
        /// </summary>
        const int MaxCachedGeometry = 1024;

        /// <summary>
        /// Total cache entries, empties included. Empties cost a dictionary slot rather than memory,
        /// but an unbounded set that grows with distance walked is a leak whatever each entry costs.
        /// </summary>
        const int MaxCachedSections = 16_384;

        /// <summary>Evict down to this fraction of a ceiling, so eviction is occasional rather than
        /// once per frame at the boundary.</summary>
        const float EvictionTarget = 0.9f;

        /// <summary>
        /// Reselect when the view axis has turned this far. The frustum used for selection is widened
        /// by <see cref="TerrainLod.FrustumMarginDegrees"/>, comfortably more than this, so a box that
        /// flips level between two reselections does it outside what is drawn.
        ///
        /// This constant is what turns selection cost into frame cost, so it is worth stating what
        /// that cost is: CollectDesired measures 0.31 ms at hip and 0.53 ms at the 3x worst case. At
        /// 5 degrees and a fast 180 deg/s turn that is one reselection every 28 ms, i.e. about 3% of a
        /// single 16.6 ms frame and nothing on the frames between. Halving this constant doubles that.
        /// </summary>
        const float ReselectDegrees = 5f;

        static readonly float ReselectCosine = MathF.Cos(ReselectDegrees * MathF.PI / 180f);

        /// <summary>Relative change in the lens that forces a reselect. Small, because this is what
        /// makes aiming refine at all, and the ADS blend is a lerp rather than a step.</summary>
        const float ReselectFovFraction = 0.02f;

        /// <summary>
        /// Hands dirty sections to the mesher threads and uploads whatever came back. Call once per frame,
        /// not once per edit. Returns how many entities were swapped in.
        ///
        /// Sections whose 3x3 chunk neighbourhood hasn't fully arrived are moved to the back of the queue
        /// and retried — that gate is what makes off-thread meshing safe, and it also means the outer ring
        /// of the loaded area produces nothing until the ring beyond it exists.
        /// </summary>
        /// <summary>
        /// Whether the ground around a position has actually been meshed yet — not merely streamed.
        ///
        /// `resolved` rather than `entities` is the right question: most boxes mesh to nothing (open
        /// air above the surface), so waiting for geometry to APPEAR would wait forever wherever the
        /// answer was legitimately "there is nothing here".
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

        public int RebuildDirty(in TerrainView view)
        {
            frameCounter++;
            RefreshLod(view);
            Dispatch();
            return Collect();
        }

        /// <summary>
        /// Recomputes which boxes should exist, and reconciles what does.
        ///
        /// Gated, because selection is no longer cheap-and-rare. It used to run only when the player
        /// crossed a chunk boundary, which was sufficient when the only input was a position. It now
        /// also has to run when the player TURNS or when the lens changes, since both move the error
        /// of every box in the world — and turning happens continuously. The gate is what keeps that
        /// from being a per-frame quadtree walk; the widened selection frustum is what keeps the gate
        /// from causing visible flicker at the frustum edge.
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
            lastForward = view.Forward;
            lastTanHalfFov = view.TanHalfFovY;
            lod.CollectDesired(view, desired);

            // Ask for anything newly wanted, and re-show anything already meshed. `resolved` is NOT
            // trimmed to the desired set any more, and that single deletion is what the cache is:
            // a box that leaves the set keeps its geometry and its settled status, so aiming across a
            // valley and lowering the rifle again costs two dictionary walks rather than meshing the
            // same few hundred boxes twice.
            //
            // The correctness that line used to provide has moved to where it belongs. It was there
            // because EnqueueDirty drops marks for boxes that are not currently desired, so a box that
            // left the set could miss an edit and come back stale. DirtySectionSink now invalidates
            // every CACHED level an edit touches instead of only the one desired level, so a stale
            // cached box is dropped at the edit rather than distrusted forever afterwards.
            //
            // The test is RESOLVED, not geometry, and the difference is the whole cost of walking.
            // Most desired boxes are open air and mesh to nothing, so they never produce an entity —
            // measured at roughly 3,000 of 4,000. Asking "do I have geometry for this?" therefore
            // answers "no" for all of them at every single reselection, and re-queues the empty three
            // quarters of the world: 2,800 sections re-meshed, eight workers pinned, thousands of
            // empty results drained on the main thread, and the urgent dig lane starved behind all of
            // it.
            foreach (var wanted in desired)
            {
                Touch(wanted);

                if (!resolved.Contains(wanted) && !inFlight.Contains(wanted)) EnqueueDirty(wanted);
            }

            // Mark what is now at the wrong level, but do NOT detach it yet — record which desired boxes
            // have to arrive first. Retiring immediately is what opened a hole at every LOD transition.
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
            // Edits first, and NOT capped by MaxDispatchScan: that cap exists to stop a huge streaming
            // backlog being walked every frame, and the urgent lane only ever holds the few sections
            // around a player's hands. Scan the whole urgent lane rather than stopping on the first
            // blocked section: repeated digs usually touch a section whose previous mesh is still in
            // flight, and that must not hold ready neighbouring sections behind it.
            int urgentScans = urgentQueue.Count;
            while (urgentScans-- > 0 && inFlight.Count < MaxInFlight && urgentQueue.Count > 0)
                TryDispatch(urgentQueue.Dequeue(), urgentQueue);

            int scans = Math.Min(dirtyQueue.Count, MaxDispatchScan);

            while (scans-- > 0 && inFlight.Count < MaxInFlight && dirtyQueue.Count > 0)
                TryDispatch(dirtyQueue.Dequeue(), dirtyQueue);
        }

        /// <summary>
        /// Submits one section, or puts it back in <paramref name="requeue"/> if it cannot go yet.
        /// Returns false only when it was requeued, so a caller draining a lane can stop rather than
        /// spin on a section that will not become dispatchable this frame.
        /// </summary>
        bool TryDispatch(LodSection section, Queue<LodSection> requeue)
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
            if (inFlight.Contains(section))
            {
                requeue.Enqueue(section);
                return false;
            }

            // Not complete yet: a worker would read voxels the main thread is still decoding.
            //
            // For a STREAMING section that means "not yet", and requeueing is how it waits. For an
            // EDIT it can mean never: an NPC digging in a chunk this client has not loaded produces
            // a dirty section whose footprint may never arrive, and the urgent lane — which is
            // rescanned in full every frame, by design, because it is meant to be tiny — accumulated
            // one entry per such edit and was still carrying a hundred of them minutes later.
            // Dropping it loses nothing: ChunkCompleted re-dirties the whole chunk if it ever
            // arrives, edit included.
            if (!terrain.FootprintComplete(section))
            {
                if (ReferenceEquals(requeue, urgentQueue))
                {
                    dirtySet.Remove(section);
                    editTiming.Remove(section);
                    return true;
                }
                requeue.Enqueue(section);
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
            long start = Stopwatch.GetTimestamp();
            int applied = 0;

            // Several batches per frame if the budget allows; the check is BETWEEN them, because a
            // batch's cost is one allocation and cannot be split part-way.
            while (true)
            {
                applied += CollectOneBatch();

                if (batch.Count == 0) break;                                  // nothing left to take
                if (Stopwatch.GetTimestamp() - start >= UploadBudgetTicks) { stats.BudgetHit(); break; }
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
            batch.Clear();
            built.Clear();

            int applied = 0;
            int vertices = 0;

            while (meshers.TryTakeResult(out var result))
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
                vertices += result.Mesh.Positions.Length;

                if (batch.Count >= MaxBatchSections || vertices >= MaxBatchVertices) break;
            }

            if (batch.Count == 0) return applied;

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

            stats.Record(hasGeometry: true, cost, factory.LastGpuTicks, batch.Count);
            return applied + batch.Count;
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
                entry.Entity.Scene = scene;
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

            if (entry.Entity is not null) entry.Entity.Scene = null;

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
            }

            attached.Remove(section);
            entry.Buffers?.Release();
            resolved.Remove(section);
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
                if (wanted)
                {
                    entity.Scene = scene;
                    attached.Add(section);
                }
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
            if (cachedGeometry <= MaxCachedGeometry && cache.Count <= MaxCachedSections) return;

            evictionScratch.Clear();
            foreach (var (section, entry) in cache)
                if (!entry.Attached && !desired.Contains(section))
                    evictionScratch.Add((entry.LastUsed, section));

            evictionScratch.Sort(static (a, b) => a.LastUsed.CompareTo(b.LastUsed));

            int geometryTarget = (int)(MaxCachedGeometry * EvictionTarget);
            int sectionTarget = (int)(MaxCachedSections * EvictionTarget);

            foreach (var (_, section) in evictionScratch)
            {
                if (cachedGeometry <= geometryTarget && cache.Count <= sectionTarget) break;
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
            int uploads;         // sections that produced geometry and so cost a GPU buffer
            int empties;         // sections that meshed to nothing; effectively free
            long uploadTicks;    // whole Swap: CPU prep + GPU buffers + scene attach
            long gpuTicks;       // just the two Buffer.New calls inside it
            long emptyTicks;
            int budgetHits;      // frames where the upload budget cut collection short
            int urgentBlocked;   // frames where an edit had sections waiting and the pool was full
            int notReady;        // meshes that came back unusable and went straight back on the queue

            int batches;

            public void Record(bool hasGeometry, long ticks, long gpu, int sections = 1)
            {
                if (hasGeometry) { uploads += sections; batches++; uploadTicks += ticks; gpuTicks += gpu; }
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
                      + $"(gpu {Ms(gpuTicks) / Math.Max(batches, 1):F2}) = {uploads / elapsed:F0} sections/s "
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

            (markingUrgent ? urgentQueue : dirtyQueue).Enqueue(section);
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
