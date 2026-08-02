using Riptide;
using RiptideMessage = Riptide.Message;
using RiptideSendMode = Riptide.MessageSendMode;

namespace Demiurge.Net
{
    /// <summary>
    /// Shared plumbing for carrying one of our messages inside a Riptide datagram.
    /// </summary>
    /// <remarks>
    /// Riptide's own <c>Add*</c>/<c>Get*</c> are not used. Our payload rides as a single length-prefixed
    /// byte blob, so the bytes on the wire are the bytes <see cref="Message"/> produced — identical to
    /// what the in-process transport carries.
    /// </remarks>
    internal static class RiptideEnvelope
    {
        internal static RiptideSendMode ToRiptide(MessageSendMode mode) => mode switch
        {
            MessageSendMode.Reliable => RiptideSendMode.Reliable,
            MessageSendMode.Unreliable => RiptideSendMode.Unreliable,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown send mode"),
        };

        internal static MessageSendMode FromRiptide(RiptideSendMode mode) => mode switch
        {
            RiptideSendMode.Reliable => MessageSendMode.Reliable,
            RiptideSendMode.Unreliable => MessageSendMode.Unreliable,
            // Notify is a Riptide channel the game has never sent on. If it ever appears here, treating it
            // as Reliable would silently understate how badly it can be delivered.
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported Riptide send mode"),
        };

        internal static RiptideMessage Wrap(Message message)
        {
            RiptideMessage wire = RiptideMessage.Create(ToRiptide(message.SendMode), message.Id);
            wire.AddBytes(message.RawBuffer, 0, message.WrittenBytes);
            return wire;
        }

        /// <summary>Decodes into one of our messages. The caller must Release the result.</summary>
        internal static Message Unwrap(MessageReceivedEventArgs e)
            => Message.CreateForRead(FromRiptide(e.Message.SendMode), e.MessageId, e.Message.GetBytes());
    }

    /// <summary>Real UDP. Used by dedicated servers, and unchanged in behaviour from before the seam existed.</summary>
    public sealed class RiptideNetServer : INetServer
    {
        private readonly Server server = new();

        public event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<NetClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<NetClientDisconnectedEventArgs>? ClientDisconnected;

        public void Start(ushort port, int maxClientCount)
        {
            server.ClientConnected += OnClientConnected;
            server.ClientDisconnected += OnClientDisconnected;
            server.MessageReceived += OnMessageReceived;
            server.Start(port, (ushort)maxClientCount, useMessageHandlers: false);
        }

        public void Stop()
        {
            server.ClientConnected -= OnClientConnected;
            server.ClientDisconnected -= OnClientDisconnected;
            server.MessageReceived -= OnMessageReceived;
            server.Stop();
        }

        public void Update() => server.Update();

        public void Send(Message message, ushort clientId)
        {
            server.Send(RiptideEnvelope.Wrap(message), clientId);
            message.Release();
        }

        public void SendToAll(Message message)
        {
            server.SendToAll(RiptideEnvelope.Wrap(message));
            message.Release();
        }

        public void Dispose() => Stop();

        private void OnClientConnected(object? sender, ServerConnectedEventArgs e)
            => ClientConnected?.Invoke(this, new NetClientConnectedEventArgs(e.Client.Id));

        private void OnClientDisconnected(object? sender, ServerDisconnectedEventArgs e)
            => ClientDisconnected?.Invoke(this, new NetClientDisconnectedEventArgs(e.Client.Id));

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            Message message = RiptideEnvelope.Unwrap(e);
            try
            {
                MessageReceived?.Invoke(
                    this,
                    new NetMessageReceivedEventArgs(e.FromConnection.Id, e.MessageId, message));
            }
            finally
            {
                message.Release();
            }
        }
    }

    /// <summary>Real UDP. Used when joining a remote server.</summary>
    public sealed class RiptideNetClient : INetClient
    {
        private readonly Client client = new();

        public event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        public event EventHandler? Connected;

        public ushort Id => client.Id;

        public void Connect(string hostAndPort)
        {
            client.MessageReceived += OnMessageReceived;
            client.Connected += OnConnected;
            client.Connect(hostAndPort, useMessageHandlers: false);
        }

        public void Disconnect()
        {
            client.Disconnect();
            client.MessageReceived -= OnMessageReceived;
            client.Connected -= OnConnected;
        }

        public void Update() => client.Update();

        public void Send(Message message)
        {
            client.Send(RiptideEnvelope.Wrap(message));
            message.Release();
        }

        public void Dispose() => Disconnect();

        private void OnConnected(object? sender, EventArgs e) => Connected?.Invoke(this, EventArgs.Empty);

        private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
        {
            Message message = RiptideEnvelope.Unwrap(e);
            try
            {
                MessageReceived?.Invoke(this, new NetMessageReceivedEventArgs(0, e.MessageId, message));
            }
            finally
            {
                message.Release();
            }
        }
    }
}
