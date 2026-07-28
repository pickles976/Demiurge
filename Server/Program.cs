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
            var options = ParseOptions(args);
            new ServerHost(options).Run(cancellation.Token);
        }

        private static ServerOptions ParseOptions(string[] args)
        {
            bool allowCheats = args.Contains("--allow-cheats", StringComparer.OrdinalIgnoreCase);
            string? mapPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].Equals("--map", StringComparison.OrdinalIgnoreCase)) continue;
                if (++i >= args.Length) throw new ArgumentException("--map requires a runtime map path");
                mapPath = Path.GetFullPath(args[i]);
            }

            return new ServerOptions { AllowCheats = allowCheats, MapPath = mapPath };
        }
    }
}
