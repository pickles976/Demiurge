using Riptide;
using Riptide.Utils;
using Demiurge;


namespace Demiurge.GameServer
{
    internal class Program
    {

        internal static Server Server {get; private set; }

        private static void Main()
        {
            
            // Dictionary<ushort, string> players = new Dictionary<ushort, string>();

            // Start
            RiptideLogger.Initialize(Console.WriteLine, Console.WriteLine, Console.WriteLine, Console.WriteLine, includeTimestamps: true);

            Server = new Server();
            Server.Start(NetworkConfig.Port, maxClientCount: 100);

            Server.ClientConnected += (object sender, ServerConnectedEventArgs e) =>
            {
                Message msg = Message.Create(MessageSendMode.Reliable, (ushort)ServerToClientId.Welcome);
                // Let the client know their ID
                msg.AddUShort(e.Client.Id);
                Server.Send(msg, e.Client.Id);

                new PlayerHandle(e.Client.Id);
            };


            bool running = true;
            Console.CancelKeyPress += (_,e) => {e.Cancel = true; running = false; };

            // Update
            while (running)
            {
                Server.Update();
                PlayerHandle.SendPositions();
                Thread.Sleep(10);
            }

            Server.Stop();

        }


    }
}

