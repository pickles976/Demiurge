using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Sends terrain over a dedicated TCP stream, one connection per client. See
    /// <see cref="ChunkTransport"/> for why terrain is not on Riptide any more.
    ///
    /// There is deliberately no rate limit in here. TCP's send window is the rate limit: a blocking
    /// write blocks while the client is behind, so a client that cannot keep up slows the writer thread
    /// instead of drowning in dropped datagrams.
    ///
    /// THREADING. Everything in this file runs off the game thread — one accept thread plus one writer
    /// thread per client — and that is only safe because it never touches a Riptide <c>Message</c>.
    /// Riptide pools those in unsynchronised static lists, which is why <see cref="ServerHost"/> has to
    /// share the client's thread in singleplayer; raw bytes on a socket have no such problem.
    ///
    /// It reads <see cref="ChunkMap"/> concurrently, which is safe today because terrain is generated
    /// once before any client connects and nothing mutates it afterwards. THAT CHANGES WITH DIGGING: an
    /// edit applied from the game thread would race a writer mid-encode, so edits will need either a
    /// copy or a per-chunk lock.
    /// </summary>
    internal sealed class ChunkTcpServer : IDisposable
    {
        readonly ChunkMap terrain;
        readonly TcpListener listener = new(IPAddress.Any, ChunkTransport.Port);
        readonly CancellationTokenSource shutdown = new();

        /// <summary>Handshake token to client id, so an incoming stream can say who it belongs to.</summary>
        readonly ConcurrentDictionary<Guid, ushort> tokens = new();

        readonly ConcurrentDictionary<ushort, Session> sessions = new();

        Thread? acceptThread;

        public ChunkTcpServer(ChunkMap terrain) => this.terrain = terrain;

        public void Start()
        {
            listener.Start();

            acceptThread = new Thread(Accept) { IsBackground = true, Name = "ChunkTcpAccept" };
            acceptThread.Start();
        }

        /// <summary>
        /// Reserves a stream for a client and returns the token it must present. Called before the
        /// client is told to connect, because the queue has to exist before the world is queued into it
        /// and before the connection arrives — the two happen in either order.
        /// </summary>
        public Guid Register(ushort clientId)
        {
            var token = Guid.NewGuid();

            sessions[clientId] = new Session();
            tokens[token] = clientId;

            return token;
        }

        /// <summary>Queues the whole map for a client. No view-distance tracking yet.</summary>
        public void QueueWorldFor(ushort clientId)
        {
            if (!sessions.TryGetValue(clientId, out var session)) return;

            for (int x = WorldGen.Min.x; x <= WorldGen.Max.x; x++)
                for (int z = WorldGen.Min.z; z <= WorldGen.Max.z; z++)
                    session.Enqueue(new ChunkIndex { x = x, z = z });
        }

        public void Forget(ushort clientId)
        {
            if (!sessions.TryRemove(clientId, out var session)) return;

            foreach (var (token, owner) in tokens)
                if (owner == clientId) tokens.TryRemove(token, out _);

            session.Dispose();
        }

        void Accept()
        {
            try
            {
                while (!shutdown.IsCancellationRequested)
                {
                    var connection = listener.AcceptTcpClient();

                    // Handshake on the accept thread: it is a 16-byte read and it decides whether this
                    // socket is worth a thread of its own.
                    var thread = new Thread(() => Serve(connection)) { IsBackground = true, Name = "ChunkTcpWriter" };
                    thread.Start();
                }
            }
            catch (SocketException) { }              // listener stopped
            catch (ObjectDisposedException) { }
        }

        void Serve(TcpClient connection)
        {
            Session? session = null;

            try
            {
                using (connection)
                {
                    // Nagle off: chunks are already ~2 KB, so coalescing only adds latency.
                    connection.NoDelay = true;

                    var stream = connection.GetStream();

                    var raw = new byte[ChunkTransport.TokenBytes];
                    stream.ReadExactly(raw);

                    if (!tokens.TryGetValue(new Guid(raw), out ushort clientId)) return;
                    if (!sessions.TryGetValue(clientId, out session)) return;

                    Write(session, stream);
                }
            }
            catch (IOException) { }                  // client went away mid-write; ordinary
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        }

        void Write(Session session, Stream stream)
        {
            var payload = new byte[ChunkTransport.MaxPayloadBytes];
            var header = new byte[ChunkTransport.HeaderBytes];

            foreach (var index in session.Consume(shutdown.Token))
            {
                if (terrain.Get(index) is not { } chunk) continue;

                // The buffer is sized for a worst-case column, so this always encodes all of it.
                var (_, length) = ChunkWire.Encode(chunk, 0, payload);

                ChunkTransport.WriteHeader(header, index, length);

                // Blocking, and that is the whole design: this is where a slow client applies
                // backpressure instead of the server having to guess a safe rate.
                stream.Write(header);
                stream.Write(payload, 0, length);
            }
        }

        public void Dispose()
        {
            shutdown.Cancel();
            listener.Stop();

            foreach (var session in sessions.Values) session.Dispose();
            sessions.Clear();
            tokens.Clear();

            acceptThread?.Join(TimeSpan.FromMilliseconds(200));
            shutdown.Dispose();
        }

        /// <summary>
        /// One client's send queue. Exists from <see cref="Register"/>, so the world can be queued before
        /// the socket arrives.
        /// </summary>
        sealed class Session : IDisposable
        {
            readonly BlockingCollection<ChunkIndex> pending = new();

            public void Enqueue(ChunkIndex index)
            {
                if (!pending.IsAddingCompleted) pending.Add(index);
            }

            public IEnumerable<ChunkIndex> Consume(CancellationToken token)
                => pending.GetConsumingEnumerable(token);

            public void Dispose()
            {
                pending.CompleteAdding();
                pending.Dispose();
            }
        }
    }
}
