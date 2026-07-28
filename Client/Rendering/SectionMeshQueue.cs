using System.Collections.Concurrent;

namespace Demiurge
{
    /// <summary>
    /// One section's geometry, produced off the main thread. <paramref name="Ready"/> is false when the
    /// scratch fill failed because a chunk the apron needed wasn't in the map — the caller should leave
    /// the section dirty and try again. An empty <paramref name="Mesh"/> with Ready true is the common
    /// case, not an error: most sections are entirely air or entirely solid.
    /// </summary>
    public readonly record struct SectionMeshResult(LodSection Section, MeshData Mesh, bool Ready);

    /// <summary>
    /// Meshes sections on worker threads. Submit sections, take finished geometry, upload it on the main
    /// thread.
    ///
    /// WHY THIS EXISTS. Meshing the streamed world is about 3.8 s of pure computation, and it used to run
    /// inside a 4 ms-per-frame budget on the main thread, so a world took the better part of a minute to
    /// appear. Worse, the budget existed because meshing on the main thread starves the same loop that
    /// pumps the network — so terrain arriving made terrain arrive more slowly.
    ///
    /// WHY THE BOUNDARY IS HERE. The obvious split is to fill the scratch buffer on the main thread
    /// (it copies, so it is a natural handoff) and mesh off it. Measured, that is the wrong place:
    /// TryFillScratch is 61% of the cost, Generate 32%, SplitCreases 8%. Filling on the main thread would
    /// leave most of the work exactly where the problem is. So workers read <see cref="ChunkMap"/>
    /// directly, and safety comes from two things instead:
    ///
    /// - the map's lookup is a ConcurrentDictionary, so inserts cannot corrupt a concurrent read;
    /// - the DISPATCHER only submits sections whose whole 3x3 chunk neighbourhood has finished
    ///   arriving, so no voxel a worker reads is still being written.
    ///
    /// Uploading stays on the main thread: <see cref="ChunkMeshFactory"/> creates GPU buffers, and
    /// off-thread resource creation is not something to gamble on given this platform's Vulkan history.
    /// </summary>
    public sealed class SectionMeshQueue : IDisposable
    {
        /// <summary>
        /// Above this angle between adjacent faces, the shared vertex is split so each side gets its own
        /// normal. Raise it if smooth terrain looks faceted, lower it if creases look soft.
        /// </summary>
        const float CreaseAngleDegrees = 50f;

        /// <summary>
        /// How far a coarse box's edge hangs down, in its OWN cell units — so it scales with the box and
        /// stays proportional to the mismatch it hides. Two cells covers the worst seam observed.
        /// </summary>
        const float SkirtDepth = 2f;

        readonly ChunkMap map;
        readonly BlockingCollection<LodSection> pending = new();
        readonly ConcurrentQueue<SectionMeshResult> finished = new();
        readonly CancellationTokenSource shutdown = new();
        readonly Thread[] workers;

        /// <summary>
        /// Leaves a core for the render thread and the in-process server, and caps out because this is
        /// bursty work that finishes — past a handful of threads the main thread's upload budget is the
        /// limit anyway, not the meshing.
        /// </summary>
        public static int DefaultWorkerCount => Math.Clamp(Environment.ProcessorCount - 1, 1, 6);

        public SectionMeshQueue(ChunkMap map, int workerCount)
        {
            this.map = map;
            workers = new Thread[Math.Max(1, workerCount)];

            for (int i = 0; i < workers.Length; i++)
            {
                // Background so a stuck worker can never hold up process exit.
                workers[i] = new Thread(Work) { IsBackground = true, Name = $"SectionMesher{i}" };
                workers[i].Start();
            }
        }

        /// <summary>Main thread. Caller must have checked the neighbourhood is complete.</summary>
        public void Submit(LodSection section)
        {
            if (shutdown.IsCancellationRequested) return;

            pending.Add(section);
        }

        /// <summary>Main thread. Results come back in whatever order the workers finish.</summary>
        public bool TryTakeResult(out SectionMeshResult result) => finished.TryDequeue(out result);

        void Work()
        {
            // Per thread, not shared: 21^3 samples, ~74 KB. Sharing one is exactly what the old
            // "one factory per meshing thread" note was warning about.
            var scratch = new Sample[ChunkMesher.ScratchVolume];

            try
            {
                foreach (var section in pending.GetConsumingEnumerable(shutdown.Token))
                {
                    if (!ChunkMesher.TryFillScratch(map, section, scratch))
                    {
                        finished.Enqueue(new SectionMeshResult(section, MeshData.Empty, Ready: false));
                        continue;
                    }

                    MeshData mesh = ChunkMesher.GenerateMesh(scratch);

                    if (mesh.Indices.Length > 0)
                    {
                        // Surface nets still shares one gradient normal across every face meeting at
                        // a cell vertex. Split only genuine creases; otherwise a sharp edge shades as
                        // a smooth fan and exposes the arbitrary triangle diagonal as a zig-zag.
                        mesh = ChunkMesher.SplitCreases(mesh, CreaseAngleDegrees);

                        // Only coarse boxes need a curtain: LOD 0 meets LOD 0 exactly, so a skirt there
                        // would be geometry nobody can ever see.
                        if (section.Level > 0) mesh = ChunkMesher.AddSkirt(mesh, SkirtDepth);
                    }

                    finished.Enqueue(new SectionMeshResult(section, mesh, Ready: true));
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a fault.
            }
        }

        public void Dispose()
        {
            shutdown.Cancel();
            pending.CompleteAdding();

            foreach (var worker in workers) worker.Join(TimeSpan.FromMilliseconds(200));

            pending.Dispose();
            shutdown.Dispose();
        }
    }
}
