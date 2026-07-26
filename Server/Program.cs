using Riptide.Utils;

namespace Demiurge.GameServer
{
    internal class Program
    {
        private static void Main()
        {
            RiptideLogger.Initialize(Console.WriteLine, Console.WriteLine,
                Console.WriteLine, Console.WriteLine, includeTimestamps: true);

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

            // The loop itself lives in ServerHost so singleplayer runs exactly the same one.
            new ServerHost().Run(cancellation.Token);
        }
    }
}
