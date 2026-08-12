using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// A whole conquest fight, headless, on the REAL map, measured for churn rather than for outcome.
///
/// The questions here cannot be asked of a hand-built scenario: whether a man keeps his squad,
/// whether a squad keeps its flag, whether the walking a man does actually carries him anywhere, and
/// whether the force spreads over the objectives or piles onto two of them. All four are ratios over
/// minutes of play against real terrain, so the flat test map answers none of them — it has no cover,
/// so nobody ever seeks any, and no slopes, so nobody is ever funnelled.
/// </summary>
[Trait("Category", "Integration")]
public class ConquestChurnDiagnosticTests
{
    private const int PerTeam = 16;
    private const uint Seconds = 420;

    [Fact]
    public void ReportChurn()
    {
        // MobDebugFeed.Enabled is a process-wide switch the developer overlay owns, and xUnit runs
        // test classes in parallel. Restore it rather than leaving every other scenario in the
        // assembly paying for a snapshot nobody reads.
        bool debugFeedWasEnabled = MobDebugFeed.Enabled;
        try
        {
            Run();
        }
        finally
        {
            MobDebugFeed.Enabled = debugFeedWasEnabled;
            MobDebugFeed.Clear();
        }
    }

    private static void Run()
    {
        string mapPath = Path.Combine(RepoRoot(), "maps", "conquest", "runtime.dmap");
        var map = RuntimeMapSerializer.Load(mapPath);
        using var harness = new MobIntegrationHarness(map.Terrain, seed: 4242);

        var flagPositions = new List<Vector3>();
        foreach (var placement in map.Placements)
            if (placement.Kind == RuntimePlacementKind.ConquestFlag)
            {
                flagPositions.Add(placement.Position);
                harness.Flags.Spawn(placement.Position);
            }

        var spawnsByTeam = map.Placements
            .Where(placement => placement.Kind == RuntimePlacementKind.PlayerSpawn)
            .GroupBy(placement => placement.Team)
            .ToDictionary(group => group.Key, group => group.Select(p => p.Position).ToArray());

        ushort nextId = 1;
        foreach (int team in spawnsByTeam.Keys.OrderBy(team => team).Take(2))
        {
            var spawns = spawnsByTeam[team];
            for (int i = 0; i < PerTeam; i++)
            {
                var origin = spawns[i % spawns.Length];
                float angle = i * 2.39996323f;
                harness.AddMob(
                    nextId++,
                    SurfaceQuery.SurfacePosition(
                        map.Terrain,
                        origin.X + MathF.Cos(angle) * (3f + i * 0.6f),
                        origin.Z + MathF.Sin(angle) * (3f + i * 0.6f)),
                    team);
            }
        }

        // Rubber-banding is a WINDOWED property: over three minutes a man who paces back and forth
        // still shows a healthy total displacement. Over five seconds he does not.
        const uint WindowTicks = 5 * NetworkConfig.TickRate;
        var windowStart = new Dictionary<ushort, Vector3>();
        var windowWalked = new Dictionary<ushort, float>();
        float windowedWalkedSum = 0f;
        float windowedDisplacementSum = 0f;
        int stalledWindows = 0;
        int movingWindows = 0;

        var lastSquad = new Dictionary<ushort, int>();
        var lastFlag = new Dictionary<ushort, uint>();
        var lastPosition = new Dictionary<ushort, Vector3>();
        var startPosition = new Dictionary<ushort, Vector3>();
        var walked = new Dictionary<ushort, float>();
        int squadChanges = 0;
        int flagChanges = 0;
        long shootingActorTicks = 0;
        long aimingActorTicks = 0;
        // What a stalled man was DOING while he failed to get anywhere. A stall rate on its own says
        // there is churn; this says which decision is producing it.
        MobDebugFeed.Enabled = true;
        var windowIntents = new Dictionary<ushort, Dictionary<string, int>>();
        var stalledIntents = new Dictionary<string, int>();
        var progressingIntents = new Dictionary<string, int>();
        // Where each man is trying to go, sampled every tick. A destination that jumps is the churn;
        // one that holds still while he fails to progress is a movement problem instead.
        var lastDestination = new Dictionary<ushort, Vector3>();
        var windowDestinationJumps = new Dictionary<ushort, int>();
        var windowDestinationTravel = new Dictionary<ushort, float>();
        // The direct signature. Rubber-banding is walking one way and then the other; rounding a
        // hillside is not, and a displacement ratio cannot tell them apart because both spend metres
        // without covering ground. A reversal is a tick whose step points more than 120 degrees away
        // from the last one, while actually moving.
        var lastStepDirection = new Dictionary<ushort, Vector3>();
        var windowReversals = new Dictionary<ushort, int>();
        long stalledReversals = 0;
        long progressingReversals = 0;
        long stalledJumps = 0;
        float stalledJumpMetres = 0f;
        long progressingJumps = 0;
        float progressingJumpMetres = 0f;
        var squadCountSamples = new List<int>();
        var flagsTargeted = new List<int>();

        foreach (var actor in harness.Actors)
        {
            lastPosition[actor.Id] = actor.Position;
            startPosition[actor.Id] = actor.Position;
            walked[actor.Id] = 0f;
            windowStart[actor.Id] = actor.Position;
            windowWalked[actor.Id] = 0f;
        }

        uint totalTicks = Seconds * NetworkConfig.TickRate;
        for (uint tick = 1; tick <= totalTicks; tick++)
        {
            harness.Step(tick, wallClockDelayMs: 0);

            foreach (var actor in harness.Actors)
            {
                if (actor.State.HasFlag(PlayerStateFlags.Shooting)) shootingActorTicks++;
                if (actor.State.HasFlag(PlayerStateFlags.Aiming)) aimingActorTicks++;
                if (MobDebugFeed.Latest.TryGetValue(actor.Id, out string? intent)
                    && !string.IsNullOrEmpty(intent))
                {
                    if (!windowIntents.TryGetValue(actor.Id, out var counts))
                        windowIntents[actor.Id] = counts = [];
                    counts[intent] = counts.GetValueOrDefault(intent) + 1;
                }
                float step = Horizontal(actor.Position, lastPosition[actor.Id]);
                walked[actor.Id] += step;
                windowWalked[actor.Id] += step;
                if (step > 0.02f)
                {
                    var direction = Vector3.Normalize(
                        (actor.Position - lastPosition[actor.Id]) with { Y = 0f });
                    if (lastStepDirection.TryGetValue(actor.Id, out var previous)
                        && Vector3.Dot(direction, previous) < -0.5f)
                        windowReversals[actor.Id] =
                            windowReversals.GetValueOrDefault(actor.Id) + 1;
                    lastStepDirection[actor.Id] = direction;
                }
                lastPosition[actor.Id] = actor.Position;
            }

            foreach (var (actorId, _, _, _, destination) in harness.Mobs.DebugAssignments())
            {
                if (lastDestination.TryGetValue(actorId, out var previous)
                    && destination != Vector3.Zero
                    && previous != Vector3.Zero)
                {
                    float moved = Horizontal(destination, previous);
                    if (moved > 1f)
                    {
                        windowDestinationJumps[actorId] =
                            windowDestinationJumps.GetValueOrDefault(actorId) + 1;
                        windowDestinationTravel[actorId] =
                            windowDestinationTravel.GetValueOrDefault(actorId) + moved;
                    }
                }
                lastDestination[actorId] = destination;
            }

            if (tick % WindowTicks == 0)
                foreach (var actor in harness.Actors)
                {
                    float wandered = windowWalked[actor.Id];
                    float progressed = Horizontal(actor.Position, windowStart[actor.Id]);
                    // A man who barely moved is standing still, which is a different complaint.
                    if (wandered >= 4f)
                    {
                        movingWindows++;
                        windowedWalkedSum += wandered;
                        windowedDisplacementSum += progressed;
                        bool stalled = progressed < wandered * 0.35f;
                        if (stalled) stalledWindows++;
                        var into = stalled ? stalledIntents : progressingIntents;
                        if (windowIntents.TryGetValue(actor.Id, out var counts))
                            foreach (var (intent, ticks) in counts)
                                into[intent] = into.GetValueOrDefault(intent) + ticks;
                        if (stalled)
                        {
                            stalledJumps += windowDestinationJumps.GetValueOrDefault(actor.Id);
                            stalledJumpMetres += windowDestinationTravel.GetValueOrDefault(actor.Id);
                            stalledReversals += windowReversals.GetValueOrDefault(actor.Id);
                        }
                        else
                        {
                            progressingJumps += windowDestinationJumps.GetValueOrDefault(actor.Id);
                            progressingJumpMetres += windowDestinationTravel.GetValueOrDefault(actor.Id);
                            progressingReversals += windowReversals.GetValueOrDefault(actor.Id);
                        }
                    }
                    windowReversals.Remove(actor.Id);
                    windowIntents.Remove(actor.Id);
                    windowDestinationJumps.Remove(actor.Id);
                    windowDestinationTravel.Remove(actor.Id);
                    windowWalked[actor.Id] = 0f;
                    windowStart[actor.Id] = actor.Position;
                }

            harness.Mobs.RecordTick(0L, harness.Actors.Count);
            if (tick % NetworkConfig.TickRate != 0) continue;

            if (tick % (60 * NetworkConfig.TickRate) == 0)
                Console.WriteLine($"[nav @{tick / NetworkConfig.TickRate}s] {harness.Mobs.Stats()}");

            var assignments = harness.Mobs.DebugAssignments().ToArray();
            squadCountSamples.Add(assignments.Select(a => (a.Team, a.Squad)).Distinct().Count());
            flagsTargeted.Add(assignments.Where(a => a.FlagId != 0).Select(a => a.FlagId).Distinct().Count());
            foreach (var (actorId, _, squad, flagId, _) in assignments)
            {
                if (lastSquad.TryGetValue(actorId, out int previousSquad)
                    && previousSquad != squad)
                    squadChanges++;
                if (lastFlag.TryGetValue(actorId, out uint previousFlag)
                    && previousFlag != flagId)
                    flagChanges++;
                lastSquad[actorId] = squad;
                lastFlag[actorId] = flagId;
            }
        }

        float totalWalked = walked.Values.Sum();
        float totalDisplacement = harness.Actors
            .Sum(actor => Horizontal(actor.Position, startPosition[actor.Id]));

        var finalAssignments = harness.Mobs.DebugAssignments().ToArray();
        var perFlag = finalAssignments
            .GroupBy(a => (a.Team, a.FlagId))
            .OrderBy(g => g.Key.Team).ThenBy(g => g.Key.FlagId)
            .Select(g => $"t{g.Key.Team}/flag{g.Key.FlagId}={g.Count()}");
        var squadSizes = finalAssignments
            .GroupBy(a => (a.Team, a.Squad))
            .Select(g => g.Count())
            .OrderBy(size => size);

        Console.WriteLine(
            $"[churn] map={map.Name} flags={flagPositions.Count} actors={harness.Actors.Count} "
            + $"seconds={Seconds}");
        Console.WriteLine(
            $"[churn] squad changes={squadChanges} "
            + $"({squadChanges / (float)harness.Actors.Count / Seconds:F3} per actor-second)");
        Console.WriteLine(
            $"[churn] flag changes={flagChanges} "
            + $"({flagChanges / (float)harness.Actors.Count / Seconds:F3} per actor-second)");
        Console.WriteLine(
            $"[churn] walked={totalWalked:F0} m displacement={totalDisplacement:F0} m "
            + $"efficiency={totalDisplacement / MathF.Max(1f, totalWalked):P0}");
        Console.WriteLine(
            $"[churn] 5s windows: moving={movingWindows}/{harness.Actors.Count * Seconds / 5} "
            + $"efficiency={windowedDisplacementSum / MathF.Max(1f, windowedWalkedSum):P0} "
            + $"stalled(<35%)={stalledWindows} ({stalledWindows / MathF.Max(1, (float)movingWindows):P0})");
        Console.WriteLine(
            $"[churn] reversals per 5s window: "
            + $"stalled={stalledReversals / MathF.Max(1, (float)stalledWindows):F2} "
            + $"progressing={progressingReversals / MathF.Max(1, (float)(movingWindows - stalledWindows)):F2}");
        Console.WriteLine(
            $"[churn] destination jumps per window: "
            + $"stalled={stalledJumps / MathF.Max(1, (float)stalledWindows):F2} "
            + $"({stalledJumpMetres / MathF.Max(1, (float)stalledWindows):F1} m) "
            + $"progressing={progressingJumps / MathF.Max(1, (float)(movingWindows - stalledWindows)):F2} "
            + $"({progressingJumpMetres / MathF.Max(1, (float)(movingWindows - stalledWindows)):F1} m)");
        Console.WriteLine(
            "[churn] stalled windows spent their ticks: "
            + Share(stalledIntents));
        Console.WriteLine(
            "[churn] progressing windows spent their ticks: "
            + Share(progressingIntents));
        Console.WriteLine(
            $"[churn] squads live: min={squadCountSamples.Min()} max={squadCountSamples.Max()} "
            + $"final sizes=[{string.Join(",", squadSizes)}]");
        Console.WriteLine(
            $"[churn] distinct flags targeted: min={flagsTargeted.Min()} "
            + $"mean={flagsTargeted.Average():F1} max={flagsTargeted.Max()} of {flagPositions.Count}");
        Console.WriteLine($"[churn] final flag spread: {string.Join(" ", perFlag)}");
        Console.WriteLine(
            $"[churn] alive t1={Alive(harness, 1)} t2={Alive(harness, 2)} "
            + $"bounds={harness.Mobs.BoundsStarted} ownContact={harness.Mobs.DiagOwnContact} "
            + $"squadThreat={harness.Mobs.DiagSquadHasThreat}");
        Console.WriteLine(
            $"[churn] shooting actor-ticks={shootingActorTicks} aiming={aimingActorTicks} "
            + $"health lost={harness.Actors.Sum(a => 100 - (a.Status?.Health.Current ?? 100))}");
        for (int i = 0; i < flagPositions.Count; i++)
            Console.WriteLine(
                $"[churn] near flag {i} ({flagPositions[i].X:F0},{flagPositions[i].Z:F0}): "
                + $"t1={NearCount(harness, 1, flagPositions[i])} "
                + $"t2={NearCount(harness, 2, flagPositions[i])}");
        Console.WriteLine(
            "[churn] flags owned: "
            + string.Join(
                " ",
                harness.Objects.All
                    .Where(o => o.Type == ObjectType.ConquestFlag)
                    .Select(o => $"#{o.NetworkId}=t{o.Team.Value}")));

        // The one number this scenario asserts, and it is deliberately the least noisy thing in it.
        //
        // Everything printed above swings run to run: the fight happens in a different place, so the
        // stall rate moves between 6% and 12% on identical code and cannot separate a regression from
        // a different afternoon. Path requests can. A man who has arrived asks for nothing, and the
        // arrive-and-ask-again loop this scenario was written to find measured 1.88, 1.91 and 2.46
        // requests per actor-second over three runs; without it, 0.46, 0.48, 0.53 and 0.86. The bound
        // sits between the two clusters with room on both sides — a guard against that loop coming
        // back, not a pin on the number.
        float requestsPerActorSecond =
            harness.Mobs.DebugPathRequests / (float)harness.Actors.Count / Seconds;
        Console.WriteLine(
            $"[churn] path requests={harness.Mobs.DebugPathRequests} "
            + $"({requestsPerActorSecond:F2} per actor-second)");
        Assert.True(
            requestsPerActorSecond < 1.4f,
            $"{requestsPerActorSecond:F2} path requests per actor-second means NPCs are replanning "
            + "rather than moving — see the holdingObjective note in MobSystem");
    }

    private static string Share(Dictionary<string, int> counts)
    {
        int total = counts.Values.Sum();
        return total == 0
            ? "(none)"
            : string.Join(
                " ",
                counts.OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{pair.Key}={pair.Value / (float)total:P0}"));
    }

    private static int Alive(MobIntegrationHarness harness, int team)
        => harness.Actors.Count(a => a.Team == team && a.Status is { Health.Current: > 0 });

    private static int NearCount(MobIntegrationHarness harness, int team, Vector3 flag)
        => harness.Actors.Count(a =>
            a.Team == team
            && a.Status is { Health.Current: > 0 }
            && Horizontal(a.Position, flag) < 20f);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("repo root not found from " + AppContext.BaseDirectory);
    }

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
