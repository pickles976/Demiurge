using System.Net.Sockets;
using Stride.Core.Diagnostics;

namespace Demiurge.GameClient
{
    /// <summary>
    /// Receives terrain over the dedicated TCP stream. See <see cref="ChunkTransport"/> for why terrain
    /// is not on Riptide.
    ///
    /// Reads on its own thread and raises <see cref="ChunkReceived"/> from it. That is safe by design
    /// rather than by luck: <see cref="TerrainState.Receive"/> only enqueues to a ConcurrentQueue and
    /// nothing touches the chunk map until <see cref="TerrainState.Drain"/> runs on the main thread. It
    /// also never touches a Riptide Message, so the unsynchronised-pool hazard that forces the
    /// in-process server onto the client's thread does not apply here.
    /// </summary>
    public sealed class ChunkTcpClient : IDisposable
    {
        // Named, not the top-level `Log` in Program.cs — the root csproj globs every .cs file, so an
        // unqualified Log here binds to that local and fails to compile.
        static readonly Logger Log = GlobalLogger.GetLogger("ChunkStream");

        /// <summary>Raised on the READER THREAD, once per whole chunk.</summary>
        public event Action<ChunkSlabsData>? ChunkReceived;

        readonly CancellationTokenSource shutdown = new();
        TcpClient? connection;
        Thread? reader;

        public void Connect(string host, Guid token)
        {
            if (reader is not null) return;   // already connected; Welcome can arrive more than once

            connection = new TcpClient { NoDelay = true };
            connection.Connect(host, ChunkTransport.Port);
            connection.GetStream().Write(token.ToByteArray());

            reader = new Thread(Read) { IsBackground = true, Name = "ChunkTcpReader" };
            reader.Start();
        }

        void Read()
        {
            var header = new byte[ChunkTransport.HeaderBytes];

            try
            {
                var stream = connection!.GetStream();

                while (!shutdown.IsCancellationRequested)
                {
                    stream.ReadExactly(header);

                    if (!ChunkTransport.TryReadHeader(header, out var index, out int length))
                    {
                        // A length this protocol cannot have produced means the framing has
                        // desynchronised. Everything after it is garbage, so stop rather than
                        // decoding noise into terrain the player then walks through.
                        Log.Error($"Chunk stream framing desynchronised at {index}; closing.");
                        return;
                    }

                    var payload = new byte[length];
                    stream.ReadExactly(payload);

                    ChunkReceived?.Invoke(new ChunkSlabsData
                    {
                        ChunkX = index.x,
                        ChunkZ = index.z,
                        FirstSlabY = 0,
                        SlabCount = ChunkConstants.ChunkHeight,
                        ChunkComplete = true,          // a frame is a whole column
                        Payload = payload,
                    });
                }
            }
            catch (EndOfStreamException) { }           // server closed; ordinary
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            shutdown.Cancel();
            connection?.Close();                       // unblocks the reader's pending read
            reader?.Join(TimeSpan.FromMilliseconds(200));
            connection?.Dispose();
            shutdown.Dispose();
        }
    }
}
