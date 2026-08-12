using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// Diagnostic for the "squads garrison a rear flag nobody is attacking" report.
///
/// Measures the ONE thing that complaint is about: how far the flag a squad is assigned to sits from
/// the nearest living enemy, and whether the squad is standing on it. A flag whose nearest enemy is
/// several hundred metres away cannot change hands inside the planning horizon, so a squad parked on
/// it is force that is doing nothing.
/// </summary>
[Trait("Category", "Integration")]
public class ObjectiveDefenceProbe
{
    private const int PerTeam = 16;
    private const uint Seconds = 180;

    [Fact]
    public void ReportRearGarrison()
    {
        string mapPath = Path.Combine(RepoRoot(), "maps", "conquest", "runtime.dmap");
        var map = RuntimeMapSerializer.Load(mapPath);
        using var harness = new MobIntegrationHarness(map.Terrain, seed: 4242);

        foreach (var placement in map.Placements)
            if (placement.Kind == RuntimePlacementKind.ConquestFlag)
                harness.Flags.Spawn(placement.Position);

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

        int squadSamples = 0;
        int safeAssignments = 0;
        int parkedOnSafe = 0;
        var byBand = new SortedDictionary<int, int>();

        uint totalTicks = Seconds * NetworkConfig.TickRate;
        for (uint tick = 1; tick <= totalTicks; tick++)
        {
            harness.Step(tick, wallClockDelayMs: 0);
            if (tick % (10 * NetworkConfig.TickRate) != 0) continue;

            foreach (int team in new[] { 1, 2 })
            {
                var snapshot = harness.Flags.StrategicSnapshot(team, harness.Actors)
                    .ToDictionary(flag => flag.FlagId);
                var squads = harness.Mobs.DebugAssignments()
                    .Where(a => a.Team == team)
                    .GroupBy(a => a.Squad);

                foreach (var squad in squads)
                {
                    uint flagId = squad.Select(a => a.FlagId).FirstOrDefault(id => id != 0);
                    if (flagId == 0 || !snapshot.TryGetValue(flagId, out var flag)) continue;

                    var members = squad
                        .Select(a => harness.Actors.First(actor => actor.Id == a.ActorId))
                        .ToArray();
                    var centre = members.Aggregate(Vector3.Zero, (sum, m) => sum + m.Position)
                        / members.Length;
                    float enemyMetres = flag.EnemyApproachSeconds * PlayerMovement.WalkSpeed;
                    float squadMetres = Horizontal(centre, flag.Position);

                    squadSamples++;
                    int band = (int)MathF.Min(400f, enemyMetres) / 50 * 50;
                    byBand[band] = byBand.GetValueOrDefault(band) + 1;
                    if (enemyMetres > 150f)
                    {
                        safeAssignments++;
                        if (squadMetres < 40f) parkedOnSafe++;
                    }

                    if (tick % (60 * NetworkConfig.TickRate) == 0)
                        Console.WriteLine(
                            $"[obj @{tick / NetworkConfig.TickRate}s] t{team} squad{squad.Key} "
                            + $"-> flag{flagId} owner={flag.OwnerTeam} "
                            + $"nearestEnemy={enemyMetres:F0}m squadDist={squadMetres:F0}m "
                            + $"friendlyNear={flag.FriendlyApproaching} "
                            + $"value={StrategicValue.Marginal(team, flag, 0, squadMetres / PlayerMovement.WalkSpeed):F3} "
                            + $"| alternatives: "
                            + string.Join(
                                ", ",
                                snapshot.Values
                                    .Where(other => other.FlagId != flagId)
                                    .Select(other =>
                                        $"f{other.FlagId}(own{other.OwnerTeam},"
                                        + $"{StrategicValue.Marginal(team, other, 0, Horizontal(centre, other.Position) / PlayerMovement.WalkSpeed):F3})")));
                }
            }
        }

        Console.WriteLine(
            $"[obj] squad-samples={squadSamples} assigned-to-flag-with-no-enemy-within-150m="
            + $"{safeAssignments} ({safeAssignments / (float)squadSamples:P0}) "
            + $"of which parked on it={parkedOnSafe}");
        Console.WriteLine(
            "[obj] nearest-enemy band of assigned flag: "
            + string.Join(", ", byBand.Select(pair => $"{pair.Key}m={pair.Value}")));
    }

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
