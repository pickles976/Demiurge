using System.Collections.Concurrent;

namespace Demiurge.GameServer;

internal enum DedicatedConsoleActionKind
{
    None,
    Stop,
    LoadMap,
}

internal readonly record struct DedicatedConsoleAction(
    DedicatedConsoleActionKind Kind,
    string? MapPath = null);

internal sealed class DedicatedServerConsole : IDisposable
{
    private readonly ConcurrentQueue<string> lines = new();
    private Thread? reader;
    private volatile bool disposed;

    public void Start()
    {
        reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Dedicated server console input",
        };
        reader.Start();
    }

    public DedicatedConsoleAction Drain(GameServer server)
    {
        var action = new DedicatedConsoleAction(DedicatedConsoleActionKind.None);
        while (lines.TryDequeue(out string? line))
        {
            string input = line.Trim();
            if (input.Length == 0) continue;
            string[] tokens = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (tokens[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(HelpText(tokens.Skip(1).ToArray()));
                continue;
            }
            if (tokens[0].Equals("items", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(ItemHelp());
                continue;
            }
            if (tokens[0].Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(server.Status());
                continue;
            }
            if (tokens[0].Equals("players", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(server.Players());
                continue;
            }
            if (tokens[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
                return new DedicatedConsoleAction(DedicatedConsoleActionKind.Stop);

            if (tokens[0].Equals("map", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Length == 2 && tokens[1].Equals("status", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(server.Status());
                    continue;
                }
                if (tokens.Length == 3 && tokens[1].Equals("load", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string path = new MapPathResolver().RuntimePath(tokens[2]);
                        _ = RuntimeMapSerializer.Load(path);
                        action = new DedicatedConsoleAction(DedicatedConsoleActionKind.LoadMap, path);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: map load failed: {ex.Message}");
                    }
                    continue;
                }
                Console.WriteLine("Error: usage: map <status|load map-name>");
                continue;
            }

            var result = server.ExecuteConsoleCommand(input);
            Console.WriteLine(result.Success ? result.Output : $"Error: {result.Output}");
        }
        return action;
    }

    internal static string HelpText(IReadOnlyList<string> topics)
    {
        if (topics.Count == 0)
            return """
                Dedicated server commands:
                  status
                  players
                  items
                  spawn mob <x> <z>
                  spawn pickup <item> <x> <z>
                  equip <@actor-id> <item>
                  ai stats
                  map status
                  map load <map-name>
                  stop
                Type 'help <command>' for details. Coordinates are X/Z; the server finds terrain Y.
                """;

        return topics[0].ToLowerInvariant() switch
        {
            "spawn" => topics.Count > 1 && topics[1].Equals("mob", StringComparison.OrdinalIgnoreCase)
                ? """
                    spawn mob <x> <z>
                      Spawns a mob on the terrain at absolute X/Z coordinates.
                      Success prints the actor ID used by 'equip', for example @60000.
                      Example: spawn mob 10 -15
                    """
                : topics.Count > 1 && topics[1].Equals("pickup", StringComparison.OrdinalIgnoreCase)
                    ? """
                        spawn pickup <item> <x> <z>
                          Spawns an item pickup on the terrain at absolute X/Z coordinates.
                          Success prints its network object ID, for example #1.
                          Example: spawn pickup ak47 0 0
                          Run 'items' to list item names.
                        """
                    : """
                        Spawn commands:
                          spawn mob <x> <z>
                          spawn pickup <item> <x> <z>
                        Examples:
                          spawn mob 10 -15
                          spawn pickup ak47 0 0
                        Run 'help spawn mob', 'help spawn pickup', or 'items' for details.
                        """,
            "equip" => """
                equip <@actor-id> <item>
                  Equips a player or mob. Run 'players' to find actor IDs.
                  Example: equip @60000 ak47
                  The dedicated console cannot use @s.
                  Equipment changes are session-only and are not written into map files.
                """,
            "ai" => """
                ai stats
                  Shows the latest 1-second average for mob movement and off-thread path searches.
                """,
            "map" => """
                Map commands:
                  map status
                  map load <map-name>
                'map load' validates maps/<map-name>/runtime.dmap before rotating the server.
                Example: map load trench-test
                """,
            "status" => "status\n  Shows the active map and total actor count.",
            "players" => "players\n  Lists connected players and mobs with IDs usable by 'equip'.",
            "items" => ItemHelp(),
            "stop" => "stop\n  Gracefully stops the dedicated server.",
            "help" => "help [command]\n  Shows all commands or detailed help for one command.",
            _ => $"No help topic named '{topics[0]}'. Type 'help' to list commands.",
        };
    }

    public void Dispose() => disposed = true;

    private static string ItemHelp()
        => "Items:\n" + string.Join(
            "\n",
            ItemCatalog.All.Select(definition =>
                $"  {definition.Id} (aliases: {string.Join(", ", definition.Aliases)})"));

    private void ReadLoop()
    {
        while (!disposed)
        {
            string? line;
            try { line = Console.ReadLine(); }
            catch (IOException) { return; }
            if (line is null) return;
            lines.Enqueue(line);
        }
    }
}
