using System.Buffers;

namespace Demiurge.Net
{
    /// <summary>
    /// A connected server/client pair living in one process, used by singleplayer instead of a loopback
    /// socket.
    /// </summary>
    /// <remarks>
    /// Two things this is NOT, both of which it would be easy and wrong to make it:
    /// <list type="number">
    /// <item><b>It is not a shortcut.</b> Every message is serialized to bytes and read back, exactly as
    /// the socket path does, so a type that fails to round-trip fails here too. Handing objects across a
    /// queue would make singleplayer pass where a real server fails.</item>
    /// <item><b>It is not a faithful imitation of Riptide.</b> It is deliberately WORSE — see
    /// <see cref="TransportHostility"/>. A localhost socket essentially never reorders and never drops,
    /// so today's singleplayer cannot catch an ordering assumption at all. This one can.</item>
    /// </list>
    /// <para>
    /// There is no transport to write here in the usual sense: no UDP, no reliability, no retransmission,
    /// no congestion control, no fragmentation, no connection negotiation. Two peers in one address space
    /// need a queue. The interesting code is all in the misbehaviour.
    /// </para>
    /// </remarks>
    public sealed class InProcessNetwork : IDisposable
    {
        /// <summary>The one client id this transport hands out. Singleplayer has exactly one player.</summary>
        internal const ushort SingleClientId = 1;

        private readonly InProcessNetServer server;
        private readonly InProcessNetClient client;

        public InProcessNetwork(int seed)
        {
            Seed = seed;
            Log = new DeliveryLog();

            // Separate generators per direction so that traffic in one direction cannot shift the
            // delivery decisions made in the other. Without this, a replay at the same seed diverges as
            // soon as the two directions interleave differently.
            var toServer = new DeliveryQueue(new Random(seed), Log, toServer: true);
            var toClient = new DeliveryQueue(new Random(seed ^ unchecked((int)0x9E3779B9)), Log, toServer: false);

            server = new InProcessNetServer(toServer, toClient);
            client = new InProcessNetClient(toServer, toClient, server);
        }

        /// <summary>Seed for this session's delivery decisions. Print it; a bug report without it is much
        /// harder to act on.</summary>
        public int Seed { get; }

        public DeliveryLog Log { get; }

        public INetServer Server => server;

        public INetClient Client => client;

        public void Dispose()
        {
            client.Dispose();
            server.Dispose();
        }
    }

    /// <summary>One direction of travel, and all of the deliberate misbehaviour.</summary>
    /// <remarks>
    /// Allocation-free on the steady path, which matters more than it looks: the server tick now runs on
    /// its own thread, so garbage produced here is collected in competition with the tick that has 33 ms
    /// to finish. Payload buffers come from <see cref="ArrayPool{T}"/> and the drain scratch is reused,
    /// so a tick's worth of replication traffic allocates nothing.
    /// </remarks>
    internal sealed class DeliveryQueue
    {
        private readonly record struct Pending(ushort Id, MessageSendMode Mode, byte[] Buffer, int Length, double DueAt);

        private static double Now
            => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        private readonly Random rng;
        private readonly DeliveryLog log;
        private readonly bool toServer;
        private readonly List<Pending> pending = [];
        private readonly object gate = new();
        private long sequence;

        // Reused across drains. Sorting by an explicit key array lets an UNSTABLE Array.Sort behave
        // stably: the key packs the jitter and the send index together, so ties break on send order.
        private Pending[] draining = new Pending[64];
        private long[] keys = new long[64];
        private int drainCount;

        internal DeliveryQueue(Random rng, DeliveryLog log, bool toServer)
        {
            this.rng = rng;
            this.log = log;
            this.toServer = toServer;
        }

        internal void Send(Message message)
        {
            // Serialize NOW, into bytes we own. This is the round trip that keeps singleplayer honest.
            ReadOnlySpan<byte> payload = message.Payload;

            if (payload.Length > Message.MaxPayloadBytes)
                throw new MessageTooLargeException(message.Id, payload.Length, Message.MaxPayloadBytes);

            lock (gate)
            {
                long seq = sequence++;

                double dropRate = message.SendMode == MessageSendMode.Reliable
                    ? TransportHostility.ReliableDropRate
                    : TransportHostility.UnreliableDropRate;

                if (rng.NextDouble() < dropRate)
                {
                    log.Record(new DeliveryRecord(seq, message.Id, message.SendMode, DeliveryVerdict.Dropped, toServer));
                    return;
                }

                Enqueue(message.Id, message.SendMode, payload);
                log.Record(new DeliveryRecord(seq, message.Id, message.SendMode, DeliveryVerdict.Delivered, toServer));

                // Riptide's unreliable channel assigns no sequence id, so nothing filters a duplicate out.
                // The copy is deliberate: two queue entries must not share one pooled buffer, or returning
                // it twice would hand the same array to two future messages.
                if (message.SendMode == MessageSendMode.Unreliable
                    && rng.NextDouble() < TransportHostility.UnreliableDuplicateRate)
                {
                    Enqueue(message.Id, message.SendMode, payload);
                    log.Record(new DeliveryRecord(seq, message.Id, message.SendMode, DeliveryVerdict.Duplicated, toServer));
                }
            }
        }

        private void Enqueue(ushort id, MessageSendMode mode, ReadOnlySpan<byte> payload)
        {
            double due = Now;
            if (NetworkConfig.SimulatedLatencySeconds > 0f)
            {
                due += NetworkConfig.SimulatedLatencySeconds
                       + (rng.NextDouble() * 2.0 - 1.0) * NetworkConfig.SimulatedJitterSeconds;
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer);
            pending.Add(new Pending(id, mode, buffer, payload.Length, due));
        }

        /// <summary>Delivers everything currently due, in a locally shuffled order.</summary>
        internal void Drain(DeliveryHandler deliver)
        {
            int count;
            lock (gate)
            {
                if (pending.Count == 0) return;

                double now = Now;
                if (draining.Length < pending.Count)
                {
                    draining = new Pending[pending.Count * 2];
                    keys = new long[pending.Count * 2];
                }

                count = 0;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].DueAt > now) continue;
                    draining[count++] = pending[i];
                    pending.RemoveAt(i);
                }

                // Back into send order: the reverse walk above collected them backwards.
                Array.Reverse(draining, 0, count);

                // Bounded shuffle. Each message gets a delivery key of (send position + jitter), where
                // jitter is in [0, ReorderWindow), and the send index breaks ties.
                //
                // This gives a provable bound rather than an approximate one: if message i is delivered
                // after message j where j > i, then i + jitter_i > j + jitter_j, so j - i < jitter_i,
                // which is less than ReorderWindow. In words — NO MESSAGE IS EVER OVERTAKEN BY ONE SENT
                // MORE THAN ReorderWindow POSITIONS LATER.
                //
                // Repeated random swaps look equivalent and are not: they let a message migrate later
                // again on each pass, so displacement grows without limit and the transport starts
                // manufacturing orderings no real network can produce.
                for (int i = 0; i < count; i++)
                    keys[i] = (long)(i + rng.Next(TransportHostility.ReorderWindow)) * count + i;

                Array.Sort(keys, draining, 0, count);
                drainCount = count;
            }

            for (int i = 0; i < drainCount; i++)
            {
                Pending item = draining[i];
                try
                {
                    deliver(item.Id, item.Mode, item.Buffer.AsSpan(0, item.Length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(item.Buffer);
                }
            }
        }
    }

    /// <summary>Receives one decoded delivery. A delegate rather than Action so the payload can be a span.</summary>
    internal delegate void DeliveryHandler(ushort id, MessageSendMode mode, ReadOnlySpan<byte> payload);

    internal sealed class InProcessNetServer : INetServer
    {
        private readonly DeliveryQueue inbound;
        private readonly DeliveryQueue outbound;

        /// <summary>
        /// Whether there is anybody to send to. Volatile because it is written by the thread that
        /// calls <see cref="InProcessNetClient.Connect"/> — the client's — and read by the server's
        /// own tick thread on every send.
        /// </summary>
        private volatile bool connected;

        internal InProcessNetServer(DeliveryQueue inbound, DeliveryQueue outbound)
        {
            this.inbound = inbound;
            this.outbound = outbound;
        }

        public event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<NetClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<NetClientDisconnectedEventArgs>? ClientDisconnected;

        public void Start(ushort port, int maxClientCount) { }

        public void Stop()
        {
            if (!connected) return;
            connected = false;
            ClientDisconnected?.Invoke(this, new NetClientDisconnectedEventArgs(InProcessNetwork.SingleClientId));
        }

        public void Update() => inbound.Drain(Deliver);

        // A send with nobody connected reaches nobody, and the message is released rather than held.
        //
        // This is the one place a queue behaves unlike a socket in a way that MATTERS, and it is not
        // hostility — it is the transport inventing a scenario no network can produce, which is the
        // same line TransportHostility's reorder bound is drawn at. A socket server iterates its
        // connected peers, so a broadcast into an empty room evaporates. Holding those messages
        // instead hands them to whoever connects next, on top of the catch-up that joining already
        // triggers, and the client is told about every pre-existing actor and object twice.
        //
        // The world is BUILT before anyone connects — every mob, its loadout, and the flags are
        // announced from GameWorld's constructor — so this was not an edge case: it was every actor
        // in the map, every session. See the conformance test named for it.
        public void Send(Message message, ushort clientId)
        {
            if (connected) outbound.Send(message);
            message.Release();
        }

        public void SendToAll(Message message)
        {
            if (connected) outbound.Send(message);
            message.Release();
        }

        public void Dispose() => Stop();

        internal void AcceptClient()
        {
            connected = true;
            ClientConnected?.Invoke(this, new NetClientConnectedEventArgs(InProcessNetwork.SingleClientId));
        }

        internal void DropClient()
        {
            if (!connected) return;
            connected = false;
            ClientDisconnected?.Invoke(this, new NetClientDisconnectedEventArgs(InProcessNetwork.SingleClientId));
        }

        private void Deliver(ushort id, MessageSendMode mode, ReadOnlySpan<byte> payload)
        {
            Message message = Message.CreateForRead(mode, id, payload);
            try
            {
                MessageReceived?.Invoke(
                    this,
                    new NetMessageReceivedEventArgs(InProcessNetwork.SingleClientId, id, message));
            }
            finally
            {
                message.Release();
            }
        }
    }

    internal sealed class InProcessNetClient : INetClient
    {
        private readonly DeliveryQueue outbound;
        private readonly DeliveryQueue inbound;
        private readonly InProcessNetServer server;

        internal InProcessNetClient(DeliveryQueue outbound, DeliveryQueue inbound, InProcessNetServer server)
        {
            this.outbound = outbound;
            this.inbound = inbound;
            this.server = server;
        }

        public event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        public event EventHandler? Connected;

        public ushort Id { get; private set; }

        public void Connect(string hostAndPort)
        {
            Id = InProcessNetwork.SingleClientId;

            // Order matches the socket path: the server sees the connection first, so whatever it sends
            // in response to ClientConnected is already queued when the client starts pumping.
            server.AcceptClient();
            Connected?.Invoke(this, EventArgs.Empty);
        }

        public void Disconnect()
        {
            server.DropClient();
            Id = 0;
        }

        public void Update() => inbound.Drain(Deliver);

        public void Send(Message message)
        {
            outbound.Send(message);
            message.Release();
        }

        public void Dispose() => Disconnect();

        private void Deliver(ushort id, MessageSendMode mode, ReadOnlySpan<byte> payload)
        {
            Message message = Message.CreateForRead(mode, id, payload);
            try
            {
                MessageReceived?.Invoke(this, new NetMessageReceivedEventArgs(0, id, message));
            }
            finally
            {
                message.Release();
            }
        }
    }
}
