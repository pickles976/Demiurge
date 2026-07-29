using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class InitialTeamSpawnPlanTests
{
    [Fact]
    public void ReservesCentralTeamOneSpawnAndBuildsFourNpcsPerTeam()
    {
        var placements = new List<RuntimePlacement>
        {
            Spawn(1, -10, 40),
            Spawn(1, 10, 40),
            Spawn(1, -12, 42),
            Spawn(1, 12, 42),
            Spawn(1, 0, 0),
            Spawn(2, -10, -40),
            Spawn(2, 10, -40),
            Spawn(2, -12, -42),
            Spawn(2, 12, -42),
        };

        var plan = InitialTeamSpawnPlan.Create(placements, playerTeam: 1, npcsPerTeam: 4);

        Assert.Equal(Vector3.Zero, plan.PlayerSpawn!.Value.Position);
        Assert.Equal(8, plan.NpcSpawns.Count);
        Assert.Equal(4, plan.NpcSpawns.Count(spawn => spawn.Team == 1));
        Assert.Equal(4, plan.NpcSpawns.Count(spawn => spawn.Team == 2));
        Assert.DoesNotContain(plan.NpcSpawns, spawn => spawn.Position == Vector3.Zero);
    }

    [Fact]
    public void RejectsScenarioWithoutEnoughDistinctTeamSpawns()
    {
        var placements = new[]
        {
            Spawn(1, 0, 0),
            Spawn(1, 1, 0),
            Spawn(2, 0, 10),
        };

        var error = Assert.Throws<InvalidDataException>(
            () => InitialTeamSpawnPlan.Create(placements, playerTeam: 1, npcsPerTeam: 4));

        Assert.Contains("team 1", error.Message);
    }

    private static RuntimePlacement Spawn(int team, float x, float z)
        => new(
            RuntimePlacementKind.PlayerSpawn,
            new Vector3(x, 0, z),
            Yaw: 0,
            Item: default,
            SpawnId: "default",
            Team: team);
}
