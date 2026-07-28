using System.Collections.Concurrent;
using System.Numerics;

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
    public class TerrainState : IClientTerrainSource
    {
        public ChunkMap Map { get; }

        /// <summary>Chunks that have received every slab, since the last drain.</summary>
        readonly HashSet<ChunkIndex> completed = new();

        /// <summary>Raised per completed chunk. The View subscribes to mesh it.</summary>
        public event Action<ChunkIndex>? ChunkCompleted;

        /// <summary>Raised per applied edit, with the world-space bounds it changed. The View
        /// subscribes to re-mesh exactly that much.</summary>
        public event Action<Vector3, Vector3>? RegionEdited;

        readonly ConcurrentQueue<ChunkSlabsData> incoming = new();
        readonly ConcurrentQueue<TerrainEditData> edits = new();

        public TerrainState()
            : this(new ChunkMap(), complete: false)
        {
        }

        public TerrainState(ChunkMap map, bool complete = true)
        {
            Map = map;
            if (!complete) return;
            foreach (var chunk in map.Snapshot()) completed.Add(chunk.index);
        }

        /// <summary>Called from the NETWORK thread. Only queues.</summary>
        public void Receive(ChunkSlabsData data) => incoming.Enqueue(data);

        /// <summary>
        /// Called from the NETWORK thread. Only queues — and that is not a formality here. Mesher
        /// WORKER threads read these voxel arrays, so writing an edit straight from the network
        /// thread would race a surface-nets pass mid-section and produce torn geometry that no
        /// later frame corrects.
        /// </summary>
        public void ReceiveEdit(TerrainEditData edit) => edits.Enqueue(edit);

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

            // After the arrivals, so an edit that lands in the same frame as the chunk it edits is
            // applied to the arrived data rather than to a chunk that is about to be overwritten by
            // it. Chunks are streamed once, so losing an edit that way would never be corrected.
            while (edits.TryDequeue(out var edit))
            {
                var (min, max) = TerrainEdits.ApplyBox(Map, edit.Centre, edit.HalfExtent, edit.Mode, edit.Fill, edit.Shape, edit.Strength);
                RegionEdited?.Invoke(min, max);
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

        /// <summary>
        /// Whether a section of this chunk can be meshed from data that will not change underneath it —
        /// the gate that makes off-thread meshing safe.
        ///
        /// A chunk is inserted into the map on its FIRST slab, so being present says nothing about being
        /// finished; <see cref="ChunkWire.Decode"/> keeps writing into its voxel array as the rest
        /// arrives. Meanwhile the mesher's apron reads
        /// <see cref="ChunkMesher.MeshDependencyRadius"/> voxels past the chunk, which can only reach the
        /// 3x3 neighbourhood. So every one of those nine has to be complete before a worker may touch
        /// them, and once complete a chunk is never rewritten (edits re-mark it dirty from the main
        /// thread instead).
        ///
        /// MAIN THREAD ONLY: <see cref="completed"/> is a plain HashSet written by <see cref="Drain"/>.
        /// The dispatcher calls this before handing work out; workers must not.
        /// </summary>
        public bool NeighbourhoodComplete(ChunkIndex index)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!IsComplete(new ChunkIndex { x = index.x + dx, z = index.z + dz })) return false;

            return true;
        }

        /// <summary>
        /// The same gate for a box at any level of detail. A coarse box's apron reaches further in world
        /// voxels — MeshDependencyRadius SAMPLES at a stride of 4 is half a chunk — so it depends on more
        /// chunks than a fine one, and the footprint has to be asked for rather than assumed to be 3x3.
        /// </summary>
        public bool FootprintComplete(LodSection section)
        {
            var (min, max) = section.ChunkFootprint();

            for (int z = min.z; z <= max.z; z++)
                for (int x = min.x; x <= max.x; x++)
                    if (!IsComplete(new ChunkIndex { x = x, z = z })) return false;

            return true;
        }

        /// <summary>
        /// Whether every chunk a body at this position collides against has fully arrived.
        ///
        /// Movement prediction needs this because unloaded terrain is IMPASSABLE in the shared step —
        /// the right answer for the world's edge, but at spawn it would wall the player in place while
        /// the server walks them normally. The body is under a metre wide, so its footprint plus the
        /// sampling reach can only touch the chunks at its four horizontal corners.
        /// </summary>
        public bool FootprintLoaded(System.Numerics.Vector3 position)
        {
            // Radius plus the voxel the trilinear sample and its central difference reach past it.
            float reach = PlayerMovement.Body.Radius + 2f;

            for (int dz = -1; dz <= 1; dz += 2)
                for (int dx = -1; dx <= 1; dx += 2)
                {
                    int worldX = (int)MathF.Floor(position.X + dx * reach);
                    int worldZ = (int)MathF.Floor(position.Z + dz * reach);

                    if (!IsComplete(ChunkTransforms.ChunkAt(worldX, worldZ))) return false;
                }

            return true;
        }

        public void Reset()
        {
            Map.Reset();
            completed.Clear();
        }
    }
}
