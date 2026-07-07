using System.Diagnostics;
using Riptide.Utils;

namespace Demiurge.GameServer
{
    internal class Program
    {
        private const float FixedDt = 1f / NetworkConfig.TickRate;

        private static void Main()
        {
            RiptideLogger.Initialize(Console.WriteLine, Console.WriteLine,
                Console.WriteLine, Console.WriteLine, includeTimestamps: true);

            var gameServer = new GameServer();
            gameServer.Start();

            bool running = true;
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

            var clock = Stopwatch.StartNew();
            double accumulator = 0, lastTime = 0;

            while (running)
            {
                double now = clock.Elapsed.TotalSeconds;
                accumulator += now - lastTime;
                lastTime = now;

                gameServer.PumpNetwork();          // pump Riptide every iteration

                while (accumulator >= FixedDt)     // simulate in fixed steps
                {
                    gameServer.Tick(FixedDt);
                    accumulator -= FixedDt;
                }

                Thread.Sleep(1);
            }

            gameServer.Stop();
        }
    }
}
