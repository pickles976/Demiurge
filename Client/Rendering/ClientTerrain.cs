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
        /// <summary>Live geometry, with the shared buffers it borrows so they can be released.</summary>
        readonly Dictionary<LodSection, (Entity Entity, SectionBuffers Buffers)> entities = new();

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

        /// <summary>
        /// Boxes at a level we no longer want, kept ON SCREEN until the boxes that replace them have
        /// actually been uploaded. Detaching on the spot leaves a hole in the terrain for as long as
        /// meshing and uploading take, which is very visible when walking across a level boundary.
        /// </summary>
        readonly List<(LodSection Old, List<LodSection> Replacements)> superseded = new();

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
            foreach (var (_, value) in entities)
            {
                value.Buffers.Release();
                value.Entity.Scene = null;
            }
            entities.Clear();
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
        /// Hands dirty sections to the mesher threads and uploads whatever came back. Call once per frame,
        /// not once per edit. Returns how many entities were swapped in.
        ///
        /// Sections whose 3x3 chunk neighbourhood hasn't fully arrived are moved to the back of the queue
        /// and retried — that gate is what makes off-thread meshing safe, and it also means the outer ring
        /// of the loaded area produces nothing until the ring beyond it exists.
        /// </summary>
        /// <summary>
        /// <paramref name="playerPosition"/> — deliberately the PLAYER, not the camera. A fly camera can
        /// sit anywhere; if it drove level selection, flying out would coarsen the terrain under
        /// inspection and flying in would refine it, so it could never show what the player actually sees.
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

        public int RebuildDirty(Vector3 lodFocus)
        {
            RefreshLod(lodFocus);
            Dispatch();
            return Collect();
        }

        /// <summary>
        /// Recomputes which boxes should exist, and reconciles what does. Only when the player crosses a
        /// chunk boundary: the quadtree walk is cheap but not free, and level boundaries are hundreds of
        /// voxels out, so nothing changes within a chunk of movement.
        /// </summary>
        void RefreshLod(Vector3 playerPosition)
        {
            var anchor = ChunkTransforms.ChunkAt(playerPosition);
            if (lodAnchor is { } previous && previous.Equals(anchor)) return;

            lodAnchor = anchor;
            TerrainLod.CollectDesired(playerPosition, desired);

            // Ask for anything newly wanted. Already-live boxes are skipped, so crossing a boundary only
            // costs the ring that actually changed level.
            foreach (var wanted in desired)
                if (!entities.ContainsKey(wanted) && !inFlight.Contains(wanted)) EnqueueDirty(wanted);

            // Mark what is now at the wrong level, but do NOT detach it yet — record which desired boxes
            // have to arrive first. Retiring immediately is what opened a hole at every LOD transition.
            foreach (var live in entities.Keys)
            {
                if (desired.Contains(live)) continue;
                if (superseded.Exists(entry => entry.Old.Equals(live))) continue;

                var replacements = new List<LodSection>();
                foreach (var wanted in desired)
                    if (Overlaps(live, wanted)) replacements.Add(wanted);

                superseded.Add((live, replacements));
            }
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

            stats.EndFrame(
                dirtyQueue.Count,
                inFlight.Count,
                urgentQueue.Count,
                urgentQueue.Count > 0 && inFlight.Count >= MaxInFlight);
            return applied;
        }

        /// <summary>
        /// Detaches superseded geometry once every box that covers it is on screen. Until then the old and
        /// new overlap, which draws that patch twice for a moment — far cheaper than a hole, and brief.
        /// </summary>
        void RetireCovered()
        {
            for (int i = superseded.Count - 1; i >= 0; i--)
            {
                var (old, replacements) = superseded[i];

                foreach (var replacement in replacements)
                    if (!resolved.Contains(replacement)) goto next;

                Detach(old);
                superseded.RemoveAt(i);

                next: ;
            }
        }

        /// <summary>Whether two boxes cover any of the same world voxels. Levels nest, so this is a box test.</summary>
        static bool Overlaps(LodSection a, LodSection b)
            => a.OriginX < b.OriginX + b.Size && b.OriginX < a.OriginX + a.Size
            && a.OriginY < b.OriginY + b.Size && b.OriginY < a.OriginY + a.Size
            && a.OriginZ < b.OriginZ + b.Size && b.OriginZ < a.OriginZ + a.Size;

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
                    Detach(result.Section);         // meshed to nothing: drop whatever was there
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
                Attach(batch[i].Section, built[i], buffers);
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

        /// <summary>Removes a section's geometry and releases its claim on the shared buffers.</summary>
        void Detach(LodSection section)
        {
            if (!entities.Remove(section, out var previous)) return;

            previous.Entity.Scene = null;
            previous.Buffers.Release();
        }

        void Attach(LodSection section, Entity entity, SectionBuffers buffers)
        {
            // Detach the old BEFORE attaching the new one, or the stale geometry stays in the scene and
            // you get two overlapping surfaces after an edit.
            Detach(section);

            entity.Scene = scene;
            entities[section] = (entity, buffers);
        }

        // ---- Diagnostics ----

        Diagnostics stats;

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

            public void EndFrame(int dirtyDepth, int inFlightCount, int urgentDepth, bool urgentStarved)
            {
                frames++;
                if (urgentStarved) urgentBlocked++;

                long now = Stopwatch.GetTimestamp();
                if (windowStart == 0) windowStart = now;

                double elapsed = (now - windowStart) / (double)Stopwatch.Frequency;
                if (elapsed < 1.0) return;

                // Nothing outstanding: stay quiet rather than logging zeroes forever.
                if (uploads + empties > 0 || dirtyDepth > 0 || inFlightCount > 0)
                {
                    double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;

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
                frames = uploads = empties = budgetHits = batches = urgentBlocked = notReady = 0;
                editSections = 0;
                editWaitTicks = editWorkTicks = editWorstTicks = 0;
                uploadTicks = emptyTicks = gpuTicks = 0;
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
        /// currently covers that part of the world. A changed voxel dirties the ONE desired box containing
        /// it — coarser levels are found by shifting, which floors correctly for negative coordinates.
        /// Only Add is ever called; the rest of ICollection exists to satisfy the signature.
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

                    if (!owner.desired.Contains(box)) continue;

                    owner.EnqueueDirty(box);
                    return;
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
