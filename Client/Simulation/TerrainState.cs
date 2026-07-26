using System.Collections.Concurrent;

namespace Demiurge.GameClient
{
    /// <summary>
    /// The client's copy of the terrain, as received from the server. Sim layer: no Stride types, so
    /// movement prediction can read it without the View being involved.
    ///
    /// The client never generates terrain. It applies what arrives and nothing else — which is what
    /// makes it structurally impossible to render a world the server hasn't sent, and what will keep
    /// it honest once player edits mean terrain is no longer a pure function of a seed.
    ///
    /// Chunks arrive as slab runs in any order (Riptide's Reliable guarantees delivery, not order), so
    /// a chunk is only announced complete when the server says its last slab has been sent.
    ///
    /// THREADING: NetworkManager dispatches on the Riptide network thread when no fake latency is
    /// configured, so <see cref="Receive"/> only enqueues. Everything that touches the map happens in
    /// <see cref="Drain"/> on the main thread. Writing the map directly from the network thread races
    /// the renderer reading it — a Dictionary insert against a concurrent lookup, which corrupts or
    /// throws rather than merely being late.
    /// </summary>
    public class TerrainState
    {
        public ChunkMap Map { get; } = new();

        /// <summary>Chunks that have received every slab, since the last drain.</summary>
        readonly HashSet<ChunkIndex> completed = new();

        /// <summary>Raised per completed chunk. The View subscribes to mesh it.</summary>
        public event Action<ChunkIndex>? ChunkCompleted;

        readonly ConcurrentQueue<ChunkSlabsData> incoming = new();

        /// <summary>Called from the NETWORK thread. Only queues.</summary>
        public void Receive(ChunkSlabsData data) => incoming.Enqueue(data);

        /// <summary>
        /// Called from the MAIN thread once per frame. Applies everything that arrived and raises
        /// <see cref="ChunkCompleted"/> for chunks that are now whole.
        /// </summary>
        public int Drain()
        {
            int applied = 0;

            while (incoming.TryDequeue(out var data))
            {
                Apply(data);
                applied++;
            }

            return applied;
        }

        void Apply(ChunkSlabsData data)
        {
            var index = new ChunkIndex { x = data.ChunkX, z = data.ChunkZ };

            // First slab run for a chunk creates it. Voxels default to density 0 — air — so a chunk
            // that is only partly arrived meshes as if the missing slabs were empty. Harmless because
            // nothing meshes it until ChunkCompleted fires.
            var chunk = Map.Get(index);
            if (chunk is null)
            {
                chunk = new TerrainChunk(index);
                Map.Insert(chunk);
            }

            ChunkWire.Decode(chunk, data.FirstSlabY, data.SlabCount, data.Payload);

            if (!data.ChunkComplete) return;

            completed.Add(index);
            ChunkCompleted?.Invoke(index);
        }

        public bool IsComplete(ChunkIndex index) => completed.Contains(index);

        public void Reset()
        {
            Map.Reset();
            completed.Clear();
        }
    }
}
