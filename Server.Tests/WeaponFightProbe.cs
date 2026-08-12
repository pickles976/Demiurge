using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// What a weapon is worth in a fight rather than on the curve: four men carrying it against four
/// riflemen on flat ground, six seeds, reported as survivors, damage, and rounds per hit.
///
/// Read every row against the Sks-vs-Sks control at the same range. The setup is not symmetric —
/// one side is spawned first and acquires first — and the control is how much of a result that is
/// worth. Asserts nothing on purpose: it is an instrument for weapon and skill tuning, in the same
/// family as ConquestChurnDiagnosticTests.
/// </summary>
[Trait("Category", "Integration")]
public class WeaponFightProbe
{
    [Theory]
    [InlineData(ItemType.Dp27, 40f)]
    [InlineData(ItemType.Ppsh, 40f)]
    [InlineData(ItemType.Sks, 40f)]
    [InlineData(ItemType.Mosin, 40f)]
    [InlineData(ItemType.Dp27, 90f)]
    [InlineData(ItemType.Ppsh, 90f)]
    [InlineData(ItemType.Sks, 90f)]
    [InlineData(ItemType.Mosin, 90f)]
    [InlineData(ItemType.Dp27, 150f)]
    [InlineData(ItemType.Sks, 150f)]
    public void FourVersusFour(ItemType weapon, float range)
    {
        int wins = 0, losses = 0, draws = 0;
        int redAliveTotal = 0, blueAliveTotal = 0;
        float redDamage = 0f, blueDamage = 0f;
        int redShots = 0, blueShots = 0;
        int redKills = 0;

        for (int seed = 0; seed < 6; seed++)
        {
            var terrain = MobIntegrationTerrain.SoilHeightmap(
                (_, _) => MobIntegrationTerrain.Ground, chunkRadius: 10);
            using var harness = new MobIntegrationHarness(terrain, seed: 1000 + seed);

            ushort id = 1;
            var red = new List<ServerPlayer>();
            var blue = new List<ServerPlayer>();
            for (int i = 0; i < 4; i++)
            {
                red.Add(harness.AddMob(
                    id++, Surface(terrain, -range * 0.5f, (i - 1.5f) * 6f), team: 1, primary: weapon));
                blue.Add(harness.AddMob(
                    id++, Surface(terrain, range * 0.5f, (i - 1.5f) * 6f), team: 2, primary: ItemType.Sks));
            }

            var ammo = new Dictionary<ushort, int>();
            foreach (var actor in harness.Actors) ammo[actor.Id] = Ammo(harness, actor);

            for (uint tick = 1; tick <= 60 * NetworkConfig.TickRate; tick++)
            {
                harness.Step(tick, wallClockDelayMs: 0);
                foreach (var actor in harness.Actors)
                {
                    int now = Ammo(harness, actor);
                    int spent = ammo[actor.Id] - now;
                    if (spent > 0)
                    {
                        if (actor.Team == 1) redShots += spent;
                        else blueShots += spent;
                    }
                    ammo[actor.Id] = now;
                }
                if (Alive(red) == 0 || Alive(blue) == 0) break;
            }

            redAliveTotal += Alive(red);
            blueAliveTotal += Alive(blue);
            redDamage += blue.Sum(Missing);
            blueDamage += red.Sum(Missing);
            redKills += blue.Count(actor => actor.Status is { Health.Current: 0 });
            if (Alive(red) > Alive(blue)) wins++;
            else if (Alive(red) < Alive(blue)) losses++;
            else draws++;
        }

        float damagePerRound = WeaponConfig.Require(weapon).Damage;
        Console.WriteLine(
            $"[gun] {weapon,-5} vs Sks at {range,3:F0}m x6: won={wins} lost={losses} drew={draws} | "
            + $"survivors {redAliveTotal / 6f:F1}v{blueAliveTotal / 6f:F1} | "
            + $"dmg {redDamage / 6f:F0}v{blueDamage / 6f:F0} | "
            + $"rounds {redShots / 6f:F0} | hits {redDamage / damagePerRound / 6f:F1} | "
            + $"hit rate {(redShots > 0 ? redDamage / damagePerRound / redShots : 0f):P1} | "
            + $"rounds/kill {(redKills > 0 ? redShots / (float)redKills : 0f):F0}");
    }

    private static int Ammo(MobIntegrationHarness harness, ServerPlayer actor)
        => harness.Weapons.TryGetPrimaryWeapon(actor, out var weapon) ? weapon.Weapon.CurrentAmmo : 0;

    private static int Alive(List<ServerPlayer> team)
        => team.Count(actor => actor.Status is { Health.Current: > 0 });

    private static float Missing(ServerPlayer actor)
        => actor.Status is { } status ? status.Health.Max - status.Health.Current : 0f;

    private static Vector3 Surface(ChunkMap terrain, float x, float z)
        => SurfaceQuery.SurfacePosition(terrain, x, z);
}
