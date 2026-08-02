using System.Buffers.Binary;
using System.Text;

namespace Demiurge.Net
{
    /// <summary>
    /// The ONLY code in this project that turns a value into bytes.
    /// </summary>
    /// <remarks>
    /// Both transports — the Riptide-backed one and the in-process one — receive an already-serialized
    /// payload and differ only in how they carry it. That is the whole parity strategy in one sentence:
    /// rather than testing that two serializers agree, there is one serializer and nothing to disagree.
    /// <para>
    /// Consequences worth knowing:
    /// <list type="bullet">
    /// <item>The <c>ComponentBundle</c> if-chain, and every <c>Messages/*.cs</c> type, is exercised
    /// identically in singleplayer and against a real server. It cannot drift between them.</item>
    /// <item>The size limit is enforced here, once, so an oversized message throws at the call site
    /// that built it instead of surfacing as a truncated read ("N unread bits") on the far end.</item>
    /// <item>We no longer use Riptide's own <c>Add*</c>/<c>Get*</c> at all. Riptide is now a datagram
    /// pipe with connection management.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The instance pool is <see cref="ThreadStaticAttribute"/> rather than a shared static list. That is
    /// the direct fix for the bug that motivated this whole exercise: Riptide's process-wide pool does
    /// <c>if (pool.Count > 0) { pool[0]; pool.RemoveAt(0); }</c> with no lock, which is safe for one
    /// peer on one thread and corrupts instantly with a client and a server allocating concurrently.
    /// Per-thread pools cannot be shared, so they cannot be raced.
    /// </para>
    /// </remarks>
    public sealed class Message
    {
        /// <summary>
        /// Largest payload one message may carry, in bytes.
        /// </summary>
        /// <remarks>
        /// Derived from Riptide 2.2.1 rather than guessed: <c>Message.MaxSize</c> is 1231 and
        /// <c>MaxPayloadSize</c> is <c>MaxSize - 6</c> = 1225. Of that, the Riptide envelope spends 2
        /// bytes on our message id and 2 more on the varint length <c>AddBytes</c> writes, leaving 1221
        /// for us. Being 4 bytes stricter than Riptide's own ceiling is the correct direction — the fake
        /// must never accept something the real transport would reject.
        /// </remarks>
        public const int MaxPayloadBytes = 1221;

        /// <summary>Bytes the Riptide envelope spends carrying one of our messages.</summary>
        internal const int RiptideEnvelopeBytes = 4;

        [ThreadStatic] private static Stack<Message>? pool;

        private byte[] buffer = new byte[MaxPayloadBytes];
        private int writeHead;
        private int readHead;

        private Message() { }

        /// <summary>The message id, which is the wire protocol. See ServerToClientId/ClientToServerId.</summary>
        public ushort Id { get; private set; }

        public MessageSendMode SendMode { get; private set; }

        /// <summary>Bytes written so far.</summary>
        public int WrittenBytes => writeHead;

        /// <summary>Bytes not yet read. Zero after a correct round trip — a non-zero value at the end of
        /// a handler means the reader and writer disagree about the field order.</summary>
        public int UnreadBytes => writeHead - readHead;

        public static Message Create<TId>(MessageSendMode mode, TId id) where TId : Enum
            => Create(mode, Convert.ToUInt16(id));

        public static Message Create(MessageSendMode mode, ushort id)
        {
            Message message = Rent();
            message.SendMode = mode;
            message.Id = id;
            return message;
        }

        /// <summary>Creates a message for reading over a payload already received.</summary>
        internal static Message CreateForRead(MessageSendMode mode, ushort id, ReadOnlySpan<byte> payload)
        {
            Message message = Rent();
            message.SendMode = mode;
            message.Id = id;
            payload.CopyTo(message.buffer);
            message.writeHead = payload.Length;
            return message;
        }

        private static Message Rent()
        {
            pool ??= new Stack<Message>();
            Message message = pool.Count > 0 ? pool.Pop() : new Message();
            message.writeHead = 0;
            message.readHead = 0;
            return message;
        }

        /// <summary>Returns this instance to the calling thread's pool. Transports call this after a send
        /// or after a received message has been fully decoded; game code does not need to.</summary>
        public void Release()
        {
            pool ??= new Stack<Message>();
            if (pool.Count < 16) pool.Push(this);
        }

        /// <summary>The written payload. Only valid until the next <see cref="Release"/>.</summary>
        internal ReadOnlySpan<byte> Payload => buffer.AsSpan(0, writeHead);

        /// <summary>Backing array, so a transport can hand <c>(array, 0, WrittenBytes)</c> straight to the
        /// wire without copying. Valid only until the next <see cref="Release"/>.</summary>
        internal byte[] RawBuffer => buffer;

        private Span<byte> Reserve(int count)
        {
            if (writeHead + count > MaxPayloadBytes)
                throw new MessageTooLargeException(Id, writeHead + count, MaxPayloadBytes);

            Span<byte> span = buffer.AsSpan(writeHead, count);
            writeHead += count;
            return span;
        }

        private ReadOnlySpan<byte> Consume(int count)
        {
            if (readHead + count > writeHead)
                throw new InvalidOperationException(
                    $"Message {Id} has {UnreadBytes} unread bytes but {count} were requested. "
                    + "The reader and writer disagree about field order.");

            ReadOnlySpan<byte> span = buffer.AsSpan(readHead, count);
            readHead += count;
            return span;
        }

        public Message AddBool(bool value)
        {
            Reserve(1)[0] = value ? (byte)1 : (byte)0;
            return this;
        }

        public bool GetBool() => Consume(1)[0] != 0;

        public Message AddByte(byte value)
        {
            Reserve(1)[0] = value;
            return this;
        }

        public byte GetByte() => Consume(1)[0];

        public Message AddUShort(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(Reserve(sizeof(ushort)), value);
            return this;
        }

        public ushort GetUShort() => BinaryPrimitives.ReadUInt16LittleEndian(Consume(sizeof(ushort)));

        public Message AddInt(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);
            return this;
        }

        public int GetInt() => BinaryPrimitives.ReadInt32LittleEndian(Consume(sizeof(int)));

        public Message AddUInt(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);
            return this;
        }

        public uint GetUInt() => BinaryPrimitives.ReadUInt32LittleEndian(Consume(sizeof(uint)));

        public Message AddFloat(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(Reserve(sizeof(float)), value);
            return this;
        }

        public float GetFloat() => BinaryPrimitives.ReadSingleLittleEndian(Consume(sizeof(float)));

        /// <summary>Length-prefixed, matching the semantics the call sites were written against.</summary>
        public Message AddBytes(byte[] value)
        {
            AddInt(value.Length);
            value.CopyTo(Reserve(value.Length));
            return this;
        }

        public byte[] GetBytes()
        {
            int length = GetInt();
            return Consume(length).ToArray();
        }

        public Message AddString(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            AddInt(byteCount);
            Encoding.UTF8.GetBytes(value, Reserve(byteCount));
            return this;
        }

        public string GetString()
        {
            int length = GetInt();
            return Encoding.UTF8.GetString(Consume(length));
        }

        public Message AddSerializable<T>(T value) where T : IMessageSerializable
        {
            value.Serialize(this);
            return this;
        }

        public T GetSerializable<T>() where T : IMessageSerializable, new()
        {
            T value = new();
            value.Deserialize(this);
            return value;
        }
    }

    /// <summary>
    /// Thrown when a message exceeds what the transport can carry.
    /// </summary>
    /// <remarks>
    /// This is deliberately a throw and not a log. The previous failure mode was silent truncation that
    /// surfaced as "N unread bits" on the far end — intermittent, remote from the cause, and recorded in
    /// CLAUDE.md as having cost real debugging time. Failing at the call site that built the message is
    /// most of the value of owning serialization.
    /// </remarks>
    public sealed class MessageTooLargeException : Exception
    {
        public MessageTooLargeException(ushort messageId, int attemptedBytes, int limitBytes)
            : base($"Message id {messageId} tried to write {attemptedBytes} bytes, over the {limitBytes}-byte limit. "
                   + "Split it, or move the payload onto the terrain-style TCP stream.")
        {
            MessageId = messageId;
            AttemptedBytes = attemptedBytes;
            LimitBytes = limitBytes;
        }

        public ushort MessageId { get; }
        public int AttemptedBytes { get; }
        public int LimitBytes { get; }
    }
}
