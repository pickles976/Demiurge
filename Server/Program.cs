using Riptide;
using Riptide.Utils;
using Demiurge;

namespace Demiurge.GameServer
  {
      internal class Program
      {
          private static void Main()
          {
              RiptideLogger.Initialize(Console.WriteLine, Console.WriteLine,
                  Console.WriteLine, Console.WriteLine, includeTimestamps: true);

              var gameServer = new GameServer();
              gameServer.Start();

              bool running = true;
              Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };

              while (running)
              {
                  gameServer.Update();     // pumps Riptide + (later) ticks the world
                  Thread.Sleep(10);        // replaced by the fixed-tick loop in Phase 6
              }

              gameServer.Stop();
          }
      }
  }


