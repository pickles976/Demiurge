using System.Numerics;
using Demiurge.GameServer;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// Reported from play: standing on a hill with a Mosin, six NPCs dug in instead of flanking a single
/// rifleman they outnumbered six to one. This runs that fight through the real MobSystem and reports
/// what they actually decided, rather than testing SquadTactics in isolation where the inputs are
/// supplied by hand and the wiring bug cannot appear.
/// </summary>
[Trait("Category", "Benchmark")]
public class HilltopDefenderProbe(ITestOutputHelper output)
{
    [Fact]
    public void WhatDoesTheSquadDoAgainstOneRifleman()
    {
        var terrain = MobIntegrationTerrain.SoilHeightmap(
            (_, _) => MobIntegrationTerrain.Ground,
            chunkRadius: 6);
        using var world = new MobIntegrationHarness(terrain, seed: 99);

        var squad = new List<ServerPlayer>();
        for (int i = 0; i < 6; i++)
            squad.Add(world.AddMob(
                (ushort)(60_000 + i),
                SurfaceQuery.SurfacePosition(terrain, -10f + i * 4f, 0f),
                team: 1,
                primary: ItemType.Ak47));

        // The player, 55 m away with a bolt gun.
        var defender = world.AddMob(
            61_000,
            SurfaceQuery.SurfacePosition(terrain, 0f, 55f),
            team: 2,
            primary: ItemType.Mosin);

        var start = squad.Select(actor => actor.Position).ToArray();
        long editsAtStart = terrain.EditVersion;

        for (uint tick = 1; tick <= 30 * NetworkConfig.TickRate; tick++)
        {
            world.Step(tick, wallClockDelayMs: 0);
            if (tick % (5 * NetworkConfig.TickRate) != 0) continue;

            output.WriteLine(
                $"t={tick / NetworkConfig.TickRate,2}s  bounds {world.Mobs.BoundsStarted,2}"
                + $"  edits {terrain.EditVersion - editsAtStart,3}"
                + $"  closed {AverageClosing(squad, start, defender.Position),6:0.0} m");
        }

        var m = world.Mobs;
        output.WriteLine("");
        output.WriteLine(
            $"actor-ticks: noOrder {m.DiagNoOrder}  roleNone {m.DiagRoleNone}"
            + $"  bound {m.DiagRoleBound}  baseOfFire {m.DiagRoleBaseOfFire}");
        output.WriteLine(
            $"movement calls: bound {m.DiagBoundMoveCalls}  cover {m.DiagCoverMoveCalls}"
            + $"  mustEntrench {m.DiagMustEntrench}");
        output.WriteLine(
            $"perception: attempts {m.DiagPerceptionAttempts}  observed {m.DiagPerceptionObserved}"
            + $" ({100.0 * m.DiagPerceptionObserved / Math.Max(1, m.DiagPerceptionAttempts):0.0}%)"
            + $"  ownContact {m.DiagOwnContact}  squadHasThreat {m.DiagSquadHasThreat}");
        output.WriteLine("");
        for (int i = 0; i < squad.Count; i++)
            output.WriteLine(
                $"  {squad[i].Id}: moved {Vector3.Distance(squad[i].Position, start[i]),5:0.0} m,"
                + $" now {Horizontal(squad[i].Position, defender.Position),5:0.0} m from the defender");

        Assert.True(true);
    }

    private static float AverageClosing(
        IReadOnlyList<ServerPlayer> squad,
        IReadOnlyList<Vector3> start,
        Vector3 defender)
    {
        float closed = 0f;
        for (int i = 0; i < squad.Count; i++)
            closed += Horizontal(start[i], defender) - Horizontal(squad[i].Position, defender);
        return closed / squad.Count;
    }

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
