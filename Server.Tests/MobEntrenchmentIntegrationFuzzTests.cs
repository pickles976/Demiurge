using System.Numerics;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

public sealed class MobEntrenchmentIntegrationFuzzTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(ItemType.Ppsh)]
    [InlineData(ItemType.Mosin)]
    [Trait("Category", "Integration")]
    public void EntrenchingNpcEntersProtectedFoxholeBeforeEngagingAcrossVariedTerrain(
        ItemType primary)
    {
        const int scenarios = 8;
        for (int seed = 0; seed < scenarios; seed++)
            Run(seed, primary);
    }

    private void Run(int seed, ItemType primary)
    {
        var random = new Random(seed * 7919 + 17);
        float slopeX = (float)(random.NextDouble() * 0.16 - 0.08);
        float slopeZ = (float)(random.NextDouble() * 0.16 - 0.08);
        var terrain = MobIntegrationTerrain.SoilHeightmap(
            (x, z) => MobIntegrationTerrain.Ground + slopeX * x + slopeZ * z,
            chunkRadius: 3);
        using var world = new MobIntegrationHarness(terrain, seed: seed + 0xF02);

        float angle = (float)(random.NextDouble() * MathF.Tau);
        var toward = new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
        Vector3 mobSpawn = SurfaceQuery.SurfacePosition(terrain, 0.5f, 0.5f);
        Vector3 enemySpawn = SurfaceQuery.SurfacePosition(
            terrain,
            mobSpawn.X + toward.X * 34f,
            mobSpawn.Z + toward.Z * 34f);
        var mob = world.AddMob(60_000, mobSpawn, primary: primary);
        var enemy = world.AddEnemy(1, enemySpawn);
        mob.Yaw = MathF.Atan2(toward.X, toward.Z);

        float grade = mob.Position.Y;
        long observedVersion = terrain.EditVersion;
        bool protectedWhileCrouched = false;
        uint protectedTick = 0;
        uint firstFireTick = 0;
        int editsWithWrongTool = 0;
        for (uint tick = 0; tick < 90 * NetworkConfig.TickRate; tick++)
        {
            world.Step(tick, wallClockDelayMs: 1);
            if (terrain.EditVersion != observedVersion)
            {
                observedVersion = terrain.EditVersion;
                if (mob.Hotbar != HotbarSlot.Shovel) editsWithWrongTool++;
            }

            if (mob.State.HasFlag(PlayerStateFlags.Crouching)
                && mob.Position.Y <= grade - 1.25f
                && ProtectedFrom(terrain, enemy.Position, mob.Position))
            {
                protectedWhileCrouched = true;
                protectedTick = tick;
            }

            bool firedPrimary = mob.Hotbar == HotbarSlot.Primary
                && mob.State.HasFlag(PlayerStateFlags.Shooting);
            if (!firedPrimary) continue;
            firstFireTick = tick;
            Assert.True(
                protectedWhileCrouched,
                $"seed {seed}: fired at tick {tick} before entering verified foxhole cover; "
              + $"position {mob.Position}, grade {grade:0.00}, crouching "
              + $"{mob.State.HasFlag(PlayerStateFlags.Crouching)}, protected now "
              + $"{ProtectedFrom(terrain, enemy.Position, mob.Position)}");
            break;
        }

        output.WriteLine(
            $"{primary} seed {seed}: slope ({slopeX:0.000},{slopeZ:0.000}), "
          + $"protected {protectedTick}, first fire {firstFireTick}, final {mob.Position}, "
          + $"enemy {enemy.Position}, edits {terrain.EditVersion}, wrong tool {editsWithWrongTool}");
        Assert.True(terrain.EditVersion > 0, $"seed {seed}: never excavated");
        Assert.Equal(0, editsWithWrongTool);
        Assert.True(protectedWhileCrouched, $"seed {seed}: never occupied protected foxhole cover");
        Assert.NotEqual(0u, firstFireTick);
    }

    private static bool ProtectedFrom(ChunkMap terrain, Vector3 threatFeet, Vector3 actorFeet)
    {
        Vector3 origin = threatFeet + Vector3.UnitY * Digging.EyeHeight;
        foreach (float height in GunConfig.AimHeights)
        {
            Vector3 target = actorFeet + Vector3.UnitY * height;
            Vector3 delta = target - origin;
            float distance = delta.Length();
            if (distance <= 1e-5f) return false;
            var hit = TerrainRaycast.Cast(terrain, origin, delta / distance, distance);
            if (hit is null || hit.Value.Distance >= distance - 0.1f)
                return false;
        }
        return true;
    }
}
