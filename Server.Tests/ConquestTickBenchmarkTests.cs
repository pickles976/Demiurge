using System.Diagnostics;
using System.Numerics;
using System.Text;
using Demiurge.GameServer;
using Demiurge.Net;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// What a conquest server tick actually costs, against the 33.3 ms budget it has to fit in 99% of
/// the time.
///
/// This runs the REAL <see cref="GameWorld"/> on the real conquest map with the real 16-per-team
/// spawn plan, so the answer includes everything a tick does — AI, actors, flags, weapons, grenades,
/// replication — rather than the one system a micro-benchmark would isolate. It is headless, which
/// is the difference between a number that can be compared across runs and a number read off a
/// running game once.
///
/// **It starts mid-match, and that is the point.** From the authored spawns the two sides need
/// several minutes to find each other, so a run of any reasonable length measures thirty-two NPCs
/// walking and never fighting — combat came out at 9 us of a 6000 us tick purely because nobody had
/// met anybody. Instead each team's home flag starts captured and both teams start ON the centre
/// flags, in contact. Combat is when the tick budget is tightest and it is the case worth measuring.
///
/// It asserts nothing about timing. A wall-clock assertion here would fail on a loaded CI box and
/// pass on an idle one, which is the failure mode CLAUDE.md warns about for the navigation budgets.
/// The output is the deliverable; read it, do not gate on it.
/// </summary>
[Collection(RealPortCollection.Name)]
public sealed class ConquestTickBenchmarkTests(ITestOutputHelper output)
{
    private const int WarmupTicks = 30;
    private const int MeasuredTicks = 30 * 120;

    /// <summary>How far from its centre flag a team's NPCs are seeded.</summary>
    private const float SpawnRingRadius = 7f;

    /// <summary>
    /// How far each team starts from the contested flag, on its own side of it. Twice this is the
    /// distance between the two sides: inside `CombatBehavior.MaxEngagementRange` (70 m) so they
    /// engage at once, and well outside `FlagConfig.CaptureRadius` so the flag is fought over rather
    /// than already held.
    /// </summary>
    private const float ContactStandoff = 20f;

    [Fact]
    [Trait("Category", "Benchmark")]
    public void ConquestTickStaysInsideItsBudget()
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(
            Path.Combine(root, "maps", "conquest", "runtime.dmap"));

        var scenario = ContactScenario.Pose(map);
        foreach (string line in scenario.Description) output.WriteLine(line);
        output.WriteLine(string.Empty);

        // GameWorld prints its own per-system breakdown once a second; route it into the test log
        // rather than reproducing the instrumentation here.
        var captured = new StringWriter();
        var previousOut = Console.Out;
        Console.SetOut(captured);

        var durations = new List<float>(MeasuredTicks);
        string outcome;
        try
        {
            // initialPlayerTeam is not optional here: InitialTeamSpawnPlan returns an EMPTY plan
            // without it, so the benchmark would measure an empty world and report a healthy tick.
            var world = new GameWorld(
                new NullNetServer(),
                scenario.Map,
                initialPlayerTeam: 1,
                initialNpcsPerTeam: 16);

            foreach (var (position, team) in scenario.HomeFlags)
                world.Flags.TryForceOwner(position, team);

            for (int i = 0; i < WarmupTicks; i++)
                world.Tick(NetworkConfig.FixedDt);

            // Paced to the real tick rate rather than run flat out. Navigation workers are separate
            // threads, so a loop that ticks as fast as it can hands them a fraction of the wall time
            // they get on a live server and makes the path queue look far worse than it is. Tick
            // COST is unaffected either way; queue depth and search latency are not.
            long tickPeriod = (long)(Stopwatch.Frequency * NetworkConfig.FixedDt);
            long nextTick = Stopwatch.GetTimestamp();
            for (int i = 0; i < MeasuredTicks; i++)
            {
                long start = Stopwatch.GetTimestamp();
                world.Tick(NetworkConfig.FixedDt);
                long end = Stopwatch.GetTimestamp();
                durations.Add((float)((end - start) * 1000.0 / Stopwatch.Frequency));

                nextTick += tickPeriod;
                int sleepMs = (int)((nextTick - Stopwatch.GetTimestamp())
                    * 1000.0 / Stopwatch.Frequency);
                if (sleepMs > 0) Thread.Sleep(sleepMs);
            }

            outcome = Casualties(world);
        }
        finally
        {
            Console.SetOut(previousOut);
        }

        var window = new PercentileWindow(
            PerformanceTargets.ServerTickBudgetMs,
            capacity: MeasuredTicks);
        foreach (float duration in durations) window.Add(duration);
        var summary = window.Summarize();

        output.WriteLine($"conquest tick over {MeasuredTicks} ticks: {summary.Format("tick")}");
        output.WriteLine(
            $"sustainable rate at p99: {1000f / MathF.Max(summary.P99, 0.001f):0.0} TPS "
            + $"(budget {PerformanceTargets.ServerTickBudgetMs:0.0} ms)");
        output.WriteLine(outcome);
        output.WriteLine(string.Empty);
        output.WriteLine(Breakdowns(captured.ToString()));
    }

    /// <summary>
    /// Evidence that the run actually reached combat. A tick cost measured over a battle nobody
    /// fought is the thing this benchmark exists to stop reporting.
    /// </summary>
    private static string Casualties(GameWorld world)
    {
        int dead = 0;
        int wounded = 0;
        int actors = 0;
        foreach (var (id, _) in world.ActorSnapshot())
        {
            if (!world.TryGetActor(id, out var actor) || actor.Status is not { } status) continue;
            actors++;
            if (status.Health.Current == 0) dead++;
            else if (status.Health.Current < status.Health.Max) wounded++;
        }
        return $"contact: {actors} actors, {dead} down, {wounded} wounded";
    }

    /// <summary>The [ServerTick] and [AI] lines GameWorld emitted while the benchmark ran.</summary>
    private static string Breakdowns(string consoleOutput)
    {
        var lines = new StringBuilder();
        foreach (string line in consoleOutput.Split('\n'))
            if (line.Contains("[ServerTick]") || line.Contains("[AI]"))
                lines.AppendLine(line.TrimEnd());
        return lines.Length == 0 ? "(no breakdown lines captured)" : lines.ToString();
    }

    /// <summary>
    /// Rewrites a conquest map into a mid-match position: each team holds its home flag and starts
    /// on a centre flag, within sight of the other team's.
    ///
    /// It edits the PLACEMENTS rather than relocating actors after the fact, so the world is built
    /// through the ordinary spawn path — no second spawn mechanism to keep in step with the real
    /// one, and no half-initialised brains from moving an actor that has already thought.
    /// </summary>
    private sealed record ContactScenario(
        RuntimeMap Map,
        IReadOnlyList<(Vector3 Position, int Team)> HomeFlags,
        IReadOnlyList<string> Description)
    {
        public static ContactScenario Pose(RuntimeMap map)
        {
            var flags = map.Placements
                .Where(placement => placement.Kind == RuntimePlacementKind.Flag)
                .Select(placement => placement.Position)
                .ToArray();
            var teams = map.Placements
                .Where(placement =>
                    placement.Kind == RuntimePlacementKind.PlayerSpawn && placement.Team > 0)
                .GroupBy(placement => placement.Team)
                .OrderBy(group => group.Key)
                .ToArray();
            if (flags.Length < teams.Length + 1)
                throw new InvalidDataException(
                    $"contact scenario needs a home flag per team plus centre flags; "
                    + $"map has {flags.Length} flags for {teams.Length} teams");

            // Home is whichever flag a team already sits next to. Everything else is a centre flag,
            // and the two sides are put on OPPOSITE SIDES OF ONE of them rather than one team per
            // centre flag: conquest's two centre flags are 239 m apart, four times the engagement
            // range, so a team per flag is still two minutes of walking and no contact. Both teams
            // converging on one objective is the firefight this benchmark is for.
            var homes = new List<(Vector3 Position, int Team)>();
            var homeSet = new HashSet<Vector3>();
            var centroids = new Dictionary<int, Vector3>();
            var description = new List<string>();

            foreach (var team in teams)
            {
                Vector3 centroid = Centroid(team.Select(spawn => spawn.Position));
                centroids[team.Key] = centroid;
                Vector3 home = flags.MinBy(flag => Vector3.DistanceSquared(flag, centroid));
                homes.Add((home, team.Key));
                homeSet.Add(home);
            }

            var contested = flags.Where(flag => !homeSet.Contains(flag)).ToArray();
            Vector3 battle = contested.MinBy(flag =>
                Vector3.DistanceSquared(flag, Centroid(centroids.Values)));

            // Each team approaches from its own half, so the fight runs along the axis the map
            // already implies rather than an arbitrary one.
            var starts = new Dictionary<int, Vector3>();
            foreach (var team in teams)
            {
                Vector3 toward = centroids[team.Key] - battle;
                toward.Y = 0f;
                Vector3 direction = toward.LengthSquared() > 1e-6f
                    ? Vector3.Normalize(toward)
                    : Vector3.UnitX;
                starts[team.Key] = battle + direction * ContactStandoff;
                description.Add(
                    $"team {team.Key}: home flag {Format(homes.First(h => h.Team == team.Key).Position)} "
                    + $"held, {team.Count()} spawns moved to {Format(starts[team.Key])}");
            }

            description.Add(
                $"contested flag {Format(battle)}; sides {2 * ContactStandoff:0} m apart");

            // Seed each team's spawns in a ring around its centre flag. The ring is deterministic
            // (golden angle, same construction InitialTeamSpawnPlan uses) so runs are comparable.
            var placements = new List<RuntimePlacement>(map.Placements.Count);
            var seeded = new Dictionary<int, int>();
            foreach (var placement in map.Placements)
            {
                if (placement.Kind != RuntimePlacementKind.PlayerSpawn
                    || placement.Team <= 0
                    || !starts.TryGetValue(placement.Team, out var centre))
                {
                    placements.Add(placement);
                    continue;
                }

                int index = seeded.TryGetValue(placement.Team, out int used) ? used : 0;
                seeded[placement.Team] = index + 1;
                float angle = (index + placement.Team * 17) * 2.39996323f;
                float radius = SpawnRingRadius * MathF.Sqrt((index + 1) / 16f);
                placements.Add(placement with
                {
                    Position = centre + new Vector3(
                        MathF.Cos(angle) * radius,
                        0f,
                        MathF.Sin(angle) * radius),
                });
            }

            return new ContactScenario(
                new RuntimeMap
                {
                    MapId = map.MapId,
                    Name = map.Name,
                    Terrain = map.Terrain,
                    Placements = placements,
                    SourceHash = map.SourceHash,
                },
                homes,
                description);
        }

        private static Vector3 Centroid(IEnumerable<Vector3> positions)
        {
            var sum = Vector3.Zero;
            int count = 0;
            foreach (var position in positions)
            {
                sum += position;
                count++;
            }
            return count == 0 ? Vector3.Zero : sum / count;
        }

        private static string Format(Vector3 v) => $"({v.X:0}, {v.Z:0})";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("repository root not found");
    }
}
