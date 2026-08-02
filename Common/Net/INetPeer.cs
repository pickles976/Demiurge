namespace Demiurge.Net
{
    /// <summary>A message that arrived. The payload is only valid for the duration of the handler.</summary>
    public sealed class NetMessageReceivedEventArgs : EventArgs
    {
        public NetMessageReceivedEventArgs(ushort clientId, ushort messageId, Message message)
        {
            ClientId = clientId;
            MessageId = messageId;
            Message = message;
        }

        /// <summary>On a server, who sent it. On a client, the sending server's id (always 0).</summary>
        public ushort ClientId { get; }

        public ushort MessageId { get; }

        public Message Message { get; }
    }

    public sealed class NetClientConnectedEventArgs : EventArgs
    {
        public NetClientConnectedEventArgs(ushort clientId) => ClientId = clientId;
        public ushort ClientId { get; }
    }

    public sealed class NetClientDisconnectedEventArgs : EventArgs
    {
        public NetClientDisconnectedEventArgs(ushort clientId) => ClientId = clientId;
        public ushort ClientId { get; }
    }

    /// <summary>
    /// The authoritative end of a connection.
    /// </summary>
    /// <remarks>
    /// Eight members, which is the entire server-side surface the game uses. Note what is absent:
    /// <c>useMessageHandlers</c>. Both ends already passed <c>false</c> and switched manually on message
    /// id, so Riptide's attribute/reflection dispatch was never in play and is not part of this contract.
    /// </remarks>
    public interface INetServer : IDisposable
    {
        event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        event EventHandler<NetClientConnectedEventArgs>? ClientConnected;
        event EventHandler<NetClientDisconnectedEventArgs>? ClientDisconnected;

        void Start(ushort port, int maxClientCount);
        void Stop();

        /// <summary>Pump inbound traffic. Handlers may run on this call or, for a real socket, on the
        /// network thread — see the marshalling note on <see cref="INetClient.Update"/>.</summary>
        void Update();

        void Send(Message message, ushort clientId);
        void SendToAll(Message message);
    }

    /// <summary>The subordinate end of a connection.</summary>
    public interface INetClient : IDisposable
    {
        event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        event EventHandler? Connected;

        /// <summary>This client's id for the session, assigned by the server.</summary>
        ushort Id { get; }

        void Connect(string hostAndPort);
        void Disconnect();

        /// <summary>
        /// Pump inbound traffic once per frame.
        /// </summary>
        /// <remarks>
        /// The Riptide implementation raises <see cref="MessageReceived"/> on the NETWORK thread, so a
        /// handler that touches main-thread state must queue rather than act — this is why
        /// <c>TerrainState</c> queues and drains in <c>Update()</c>. The in-process implementation
        /// delivers on the calling thread, which is more forgiving; do not let that difference tempt
        /// anyone into removing the marshalling.
        /// </remarks>
        void Update();

        void Send(Message message);
    }
}
