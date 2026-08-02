namespace Demiurge.Net
{
    /// <summary>
    /// A server that accepts sends and discards them. For headless tests that exercise server systems
    /// without a network.
    /// </summary>
    /// <remarks>
    /// This replaces tests constructing a real <c>Riptide.Server</c> and relying on an unstarted socket
    /// to swallow the traffic. Same effect, but stated rather than incidental — and it takes Riptide out
    /// of the test projects entirely.
    /// <para>
    /// It still releases every message it is handed, so a test that leaks pooled messages fails here the
    /// same way it would in production.
    /// </para>
    /// </remarks>
    public sealed class NullNetServer : INetServer
    {
        public event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<NetClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<NetClientDisconnectedEventArgs>? ClientDisconnected;

        public void Start(ushort port, int maxClientCount) { }
        public void Stop() { }
        public void Update() { }
        public void Send(Message message, ushort clientId) => message.Release();
        public void SendToAll(Message message) => message.Release();
        public void Dispose() { }

        /// <summary>Raises <see cref="ClientConnected"/>, so a test can drive the connect path.</summary>
        public void SimulateConnect(ushort clientId)
            => ClientConnected?.Invoke(this, new NetClientConnectedEventArgs(clientId));

        /// <summary>Raises <see cref="ClientDisconnected"/>, so a test can drive the disconnect path.</summary>
        public void SimulateDisconnect(ushort clientId)
            => ClientDisconnected?.Invoke(this, new NetClientDisconnectedEventArgs(clientId));

        /// <summary>Raises <see cref="MessageReceived"/> as though a client had sent it.</summary>
        public void SimulateReceive(ushort clientId, Message message)
            => MessageReceived?.Invoke(this, new NetMessageReceivedEventArgs(clientId, message.Id, message));
    }
}
