using Riptide;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Sends terrain to clients. The server generates and owns it; clients never generate any.
    ///
    /// Throttled rather than dumped. Riptide's reliable layer has no congestion control, so bursting
    /// a whole map at once can self-inflict the packet loss it then has to retransmit through. A few
    /// messages per tick clears the fixed map in well under a second and is the same shape per-player
    /// streaming will need.
    /// </summary>
    internal class ChunkStreamer
    {
        /// <summary>
        /// Messages per client per tick. Deliberately modest: the reliable channel has no congestion
        /// control, and the first ticks after a join are also carrying the handshake, player spawns and
        /// object catch-up. At 8/tick the ~255-message fixed map lands in about a second.
        /// </summary>
        const int MessagesPerTick = 8;

        readonly Server server;
        readonly ChunkMap terrain;

        /// <summary>What each client still needs, as a cursor per pending chunk.</summary>
        readonly Dictionary<ushort, Queue<ChunkIndex>> pending = new();
        readonly Dictionary<ushort, int> slabCursor = new();

        readonly byte[] buffer = new byte[MaxPayloadBytes];

        /// <summary>
        /// Leaves room for the message's own fields inside Riptide's 1225-byte payload. A raw slab is
        /// 513 bytes, so this fits two of them per message in the worst case and many more when
        /// they're uniform.
        /// </summary>
        const int MaxPayloadBytes = 1100;

        public ChunkStreamer(Server server, ChunkMap terrain)
        {
            this.server = server;
            this.terrain = terrain;
        }

        /// <summary>Queues the whole map for a joining client. No view-distance tracking yet.</summary>
        public void QueueWorldFor(ushort clientId)
        {
            var queue = new Queue<ChunkIndex>();

            for (int x = WorldGen.Min.x; x <= WorldGen.Max.x; x++)
            {
                for (int z = WorldGen.Min.z; z <= WorldGen.Max.z; z++)
                    queue.Enqueue(new ChunkIndex { x = x, z = z });
            }

            pending[clientId] = queue;
            slabCursor[clientId] = 0;
        }

        public void Forget(ushort clientId)
        {
            pending.Remove(clientId);
            slabCursor.Remove(clientId);
        }

        /// <summary>Sends this tick's allowance to every client still catching up.</summary>
        public void Tick()
        {
            foreach (ushort clientId in pending.Keys.ToArray())
            {
                var queue = pending[clientId];

                for (int sent = 0; sent < MessagesPerTick && queue.Count > 0; sent++)
                {
                    var index = queue.Peek();

                    if (terrain.Get(index) is not { } chunk)   // shouldn't happen; don't wedge the queue
                    {
                        queue.Dequeue();
                        slabCursor[clientId] = 0;
                        continue;
                    }

                    int firstSlab = slabCursor[clientId];
                    var (slabCount, byteCount) = ChunkWire.Encode(chunk, firstSlab, buffer);

                    // A single slab always fits, so zero slabs means the buffer is mis-sized rather
                    // than the chunk being finished — bail loudly instead of spinning forever.
                    if (slabCount == 0)
                        throw new InvalidOperationException($"Payload budget {MaxPayloadBytes} cannot hold one slab.");

                    int nextSlab = firstSlab + slabCount;
                    bool complete = nextSlab >= ChunkConstants.ChunkHeight;

                    var message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ChunkSlabs);
                    message.AddSerializable(new ChunkSlabsData
                    {
                        ChunkX = index.x,
                        ChunkZ = index.z,
                        FirstSlabY = (ushort)firstSlab,
                        SlabCount = (ushort)slabCount,
                        ChunkComplete = complete,
                        Payload = buffer[..byteCount],
                    });
                    server.Send(message, clientId);

                    if (complete)
                    {
                        queue.Dequeue();
                        slabCursor[clientId] = 0;
                    }
                    else
                    {
                        slabCursor[clientId] = nextSlab;
                    }
                }

                if (queue.Count == 0) Forget(clientId);
            }
        }
    }
}
