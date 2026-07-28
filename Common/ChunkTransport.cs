using System.Buffers.Binary;

namespace Demiurge
{
    /// <summary>
    /// Framing for the bulk terrain stream, shared by both ends so the two can't drift apart.
    ///
    /// WHY TERRAIN LEFT RIPTIDE. Gameplay traffic and bulk traffic want opposite things. Riptide is UDP
    /// and its reliable channel has no congestion control, so the only throttle was a hand-picked
    /// messages-per-tick constant — and a constant is not flow control. Raising it to make terrain load
    /// faster killed a LOCALHOST connection: the server emitted its whole allowance unpaced inside one
    /// frame, the client couldn't drain its socket while meshing, datagrams dropped, and retransmits
    /// lengthened the frames that caused the drops.
    ///
    /// TCP fixes that structurally rather than by tuning. A blocking write blocks when the send window
    /// is full, which IS backpressure, so there is no rate constant here at all. Head-of-line blocking
    /// is TCP's weakness and it does not matter for bulk transfer where every byte is wanted anyway,
    /// and chunk data is latency-tolerant in a way player movement is not.
    ///
    /// Two consequences worth knowing. Riptide's 1225-byte datagram limit is gone, so a frame carries a
    /// WHOLE chunk column instead of a slab run — the slab cursor and its resume logic disappear. And
    /// the stream is ordered, so the "every message independently applicable" rule no longer has to
    /// hold for terrain; it is kept anyway because it costs nothing and delta-coding across slabs is a
    /// later optimisation.
    ///
    /// FRAMING IS THE NEW FAILURE MODE. Over UDP a corrupt datagram was one bad chunk. Over a stream, a
    /// wrong length desynchronises everything after it, so the length is validated on read rather than
    /// trusted.
    /// </summary>
    public static class ChunkTransport
    {
        /// <summary>One past the Riptide port. Both ends derive it, so there is one number to change.</summary>
        public const ushort Port = NetworkConfig.Port + 1;

        /// <summary>Payload length, chunk x, chunk z — little-endian int32 each.</summary>
        public const int HeaderBytes = sizeof(int) * 3;

        /// <summary>A Guid, sent by the client as the first bytes to say which player it is.</summary>
        public const int TokenBytes = 16;

        /// <summary>
        /// Worst case for one whole column, which is what the send buffer must be sized to. Every slab
        /// falling back to raw is unreachable with today's terrain but the buffer must still fit it.
        /// </summary>
        public static int MaxPayloadBytes => ChunkWire.MaxSlabBytes * ChunkConstants.ChunkHeight;

        public static void WriteHeader(Span<byte> destination, ChunkIndex index, int payloadLength)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination[..4], payloadLength);
            BinaryPrimitives.WriteInt32LittleEndian(destination[4..8], index.x);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..12], index.z);
        }

        /// <summary>
        /// False when the length is not something this protocol could have produced — which over a
        /// stream means the framing has desynchronised and the connection is unrecoverable, not that one
        /// chunk is bad.
        /// </summary>
        public static bool TryReadHeader(ReadOnlySpan<byte> source, out ChunkIndex index, out int payloadLength)
        {
            payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[..4]);

            index = new ChunkIndex
            {
                x = BinaryPrimitives.ReadInt32LittleEndian(source[4..8]),
                z = BinaryPrimitives.ReadInt32LittleEndian(source[8..12])
            };

            return payloadLength > 0 && payloadLength <= MaxPayloadBytes;
        }
    }
}
