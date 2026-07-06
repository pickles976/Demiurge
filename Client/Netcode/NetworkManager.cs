using Riptide;
using Stride.Core.Diagnostics;

namespace Demiurge.GameClient
{
    public static class NetworkManager
    {
        private static readonly Logger Log = GlobalLogger.GetLogger("Network");
        internal static Client Client;

        /// The id of this client that was assigned by the server during this session
        public static ushort ClientId { get; private set; } = 0;

        public static void Connect()
        {
            Client = new Client();
            Client.Connected += (_, _) => Log.Info("Connected to server");
            Client.ConnectionFailed += (_, _) => Log.Warning("Connection to server failed");
            Client.Disconnected += (_, _) => Log.Info("Disconnected from server");
            Client.Connect($"127.0.0.1:{NetworkConfig.Port}");
        }

        /// <summary>Pump once per frame from Program.cs Update().</summary>
        public static void Update() => Client?.Update();

        // Riptide finds this by reflection via the attribute. It MUST be static —
        // Riptide throws NonStaticHandlerException otherwise. The ushort here must
        // match what the server put in Message.Create.
        [MessageHandler((ushort)ServerToClientId.Welcome)]
        private static void HandleWelcome(Message message)
        {
            // Reads must happen in the same order as the Adds on the server.
            ClientId = message.GetUShort();
            Log.Info($"Server says: Welcome! Your Client ID is: {ClientId}");
            GameEvents.ServerMessageReceived.Broadcast();   // "A": the signal
        }
    }
}