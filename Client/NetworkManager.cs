using Riptide;
using Stride.Core.Diagnostics;

namespace Demiurge
{
    public static class NetworkManager
    {
        private static readonly Logger Log = GlobalLogger.GetLogger("Network");
        private static Client? _client;

        /// <summary>Last message text from the server ("B" in the A+B pattern).</summary>
        public static string LastServerMessage { get; private set; } = "—";

        public static void Connect()
        {
            _client = new Client();
            _client.Connected += (_, _) => Log.Info("Connected to server");
            _client.ConnectionFailed += (_, _) => Log.Warning("Connection to server failed");
            _client.Disconnected += (_, _) => Log.Info("Disconnected from server");
            _client.Connect($"127.0.0.1:{NetworkConfig.Port}");
        }

        /// <summary>Pump once per frame from Program.cs Update().</summary>
        public static void Update() => _client?.Update();

        // Riptide finds this by reflection via the attribute. It MUST be static —
        // Riptide throws NonStaticHandlerException otherwise. The ushort here must
        // match what the server put in Message.Create.
        [MessageHandler((ushort)ServerToClientId.Welcome)]
        private static void HandleWelcome(Message message)
        {
            // Reads must happen in the same order as the Adds on the server.
            LastServerMessage = message.GetString();
            Log.Info($"Server says: {LastServerMessage}");
            GameEvents.ServerMessageReceived.Broadcast();   // "A": the signal
        }
    }
}