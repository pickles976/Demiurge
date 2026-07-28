using Riptide.Utils;

namespace Demiurge.GameServer
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            RiptideLogger.Initialize(Console.WriteLine, Console.WriteLine,
                Console.WriteLine, Console.WriteLine, includeTimestamps: true);

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

            // The loop itself lives in ServerHost so singleplayer runs exactly the same one.
            bool allowCheats = args.Contains("--allow-cheats", StringComparer.OrdinalIgnoreCase);
            new ServerHost(allowCheats).Run(cancellation.Token);
        }
    }
}
