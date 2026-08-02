using System.Diagnostics;
using System.Numerics;
using Demiurge.GameServer;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

public sealed class MobNavigationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Integration")]
    public void ConquestNpcsLeaveTheirSpawnBases()
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(
            Path.Combine(root, "maps", "conquest", "runtime.dmap"));
        var plan = InitialTeamSpawnPlan.Create(
            map.Placements,
            playerTeam: 1,
            npcsPerTeam: 16);
        using var world = new MobIntegrationHarness(map.Terrain, seed: 0xC0A9);
        foreach (var flag in map.Placements.Where(p => p.Kind == RuntimePlacementKind.Flag))
            world.Flags.Spawn(flag.Position);

        var starts = new Dictionary<ushort, Vector3>();
        ushort id = 60_000;
        foreach (var spawn in plan.NpcSpawns)
        {
            Vector3 position = NavTraversal.TryFindNearestStandable(
                    map.Terrain,
                    spawn.Position,
                    horizontalRadius: 8,
                    out var spawnCell)
                ? NavTraversal.Position(map.Terrain, spawnCell)
                : spawn.Position;
            var mob = world.AddMob(id++, position, spawn.Team);
            starts[mob.Id] = mob.Position;
        }

        for (uint tick = 0; tick < 20 * NetworkConfig.TickRate; tick++)
            world.Step(tick, wallClockDelayMs: 3);

        foreach (var mob in world.Actors.Where(actor => actor.IsMob))
            output.WriteLine(
                $"mob {mob.Id} team {mob.Team}: {starts[mob.Id]} -> {mob.Position} "
              + $"({MathF.Sqrt(HorizontalDistanceSquared(starts[mob.Id], mob.Position)):0.0} m)");

        Assert.All(
            world.Actors.Where(actor => actor.IsMob),
            mob => Assert.True(
                HorizontalDistanceSquared(starts[mob.Id], mob.Position) >= 4f,
                $"mob {mob.Id} remained at its spawn: {starts[mob.Id]} -> {mob.Position}"));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(1)]
    [InlineData(2)]
    public void EachConquestTeamReachesBothCentralFlagsAcrossTheDitch(int team)
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(
            Path.Combine(root, "maps", "conquest", "runtime.dmap"));
        var plan = InitialTeamSpawnPlan.Create(
            map.Placements,
            playerTeam: 1,
            npcsPerTeam: 16);
        var flags = map.Placements
            .Where(placement => placement.Kind == RuntimePlacementKind.Flag)
            .ToArray();
        Assert.Equal(4, flags.Length);
        var centralFlags = flags
            .OrderBy(flag => flag.Position.X * flag.Position.X
                           + flag.Position.Z * flag.Position.Z)
            .Take(2)
            .ToArray();

        using var world = new MobIntegrationHarness(map.Terrain, seed: 0xD17C + team);
        foreach (var flag in flags)
            world.Flags.Spawn(flag.Position);

        ushort id = (ushort)(60_000 + team * 100);
        foreach (var spawn in plan.NpcSpawns.Where(spawn => spawn.Team == team))
        {
            Vector3 position = NavTraversal.TryFindNearestStandable(
                    map.Terrain,
                    spawn.Position,
                    horizontalRadius: 8,
                    out var spawnCell)
                ? NavTraversal.Position(map.Terrain, spawnCell)
                : spawn.Position;
            _ = world.AddMob(id++, position, team);
        }
        Assert.Equal(16, world.Actors.Count(actor => actor.IsMob && actor.Team == team));

        var reached = new bool[centralFlags.Length];
        var closestSquared = Enumerable.Repeat(float.PositiveInfinity, centralFlags.Length).ToArray();
        uint completedTick = 0;
        const uint maximumTicks = 240 * NetworkConfig.TickRate;
        float captureRadiusSquared = FlagConfig.CaptureRadius * FlagConfig.CaptureRadius;
        for (uint tick = 0; tick < maximumTicks; tick++)
        {
            world.Step(tick, wallClockDelayMs: 2);
            for (int flagIndex = 0; flagIndex < centralFlags.Length; flagIndex++)
                foreach (var mob in world.Actors.Where(actor => actor.IsMob && actor.Team == team))
                {
                    float distanceSquared = Vector3.DistanceSquared(
                        mob.Position,
                        centralFlags[flagIndex].Position);
                    closestSquared[flagIndex] = MathF.Min(
                        closestSquared[flagIndex],
                        distanceSquared);
                    reached[flagIndex] |= distanceSquared <= captureRadiusSquared;
                }

            if (!reached.All(value => value)) continue;
            completedTick = tick;
            break;
        }

        for (int flagIndex = 0; flagIndex < centralFlags.Length; flagIndex++)
            output.WriteLine(
                $"team {team} central flag {centralFlags[flagIndex].Position}: "
              + $"reached {reached[flagIndex]}, closest {MathF.Sqrt(closestSquared[flagIndex]):0.0} m");
        output.WriteLine(
            $"team {team} reached both central flags at tick {completedTick}/{maximumTicks}");

        Assert.All(
            Enumerable.Range(0, centralFlags.Length),
            flagIndex => Assert.True(
                reached[flagIndex],
                $"team {team} never reached central flag {centralFlags[flagIndex].Position}; "
              + $"closest approach was {MathF.Sqrt(closestSquared[flagIndex]):0.0} m"));
        Assert.NotEqual(0u, completedTick);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RelocatedFormationMemberResumesItsObjectiveInsteadOfScanningAtSpawn()
    {
        var terrain = MobIntegrationTerrain.SoilHeightmap((_, _) => MobIntegrationTerrain.Ground);
        using var world = new MobIntegrationHarness(terrain, seed: 0xA11);
        var flag = new Vector3(0.5f, MobIntegrationTerrain.Ground, 0.5f);
        world.Flags.Spawn(flag);
        _ = world.AddMob(60_000, flag + new Vector3(0f, 0f, -1f));
        var relocated = world.AddMob(60_001, flag + new Vector3(1f, 0f, 0f));

        world.Step(0);
        world.Relocate(relocated, flag);
        Vector3 start = relocated.Position;
        uint movedTick = 0;
        for (uint tick = 1; tick < 15 * NetworkConfig.TickRate; tick++)
        {
            world.Step(tick);
            if (HorizontalDistanceSquared(start, relocated.Position) > 2f * 2f)
            {
                movedTick = tick;
                break;
            }
        }

        output.WriteLine($"relocated {start}; final {relocated.Position}; moved tick {movedTick}");
        Assert.NotEqual(0u, movedTick);
        Assert.False(world.Mobs.TryDequeueStuckMob(out _));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void NpcExcavatesAcrossAnUnwalkableSoilSlopeInsteadOfJumpingAtIt()
    {
        const float degrees = PlayerMovement.MaxSlopeDegrees + 7f;
        const float rampLength = 3f;
        float rise = MathF.Tan(degrees * MathF.PI / 180f);
        float plateau = MobIntegrationTerrain.Ground + rise * rampLength;
        var terrain = MobIntegrationTerrain.SoilHeightmap((x, _) =>
            x <= 0
                ? MobIntegrationTerrain.Ground
                : x < rampLength
                    ? MobIntegrationTerrain.Ground + rise * x
                    : plateau);
        using var world = new MobIntegrationHarness(terrain, seed: 0x510);
        world.Flags.Spawn(new Vector3(7.5f, plateau, 0.5f));
        var mob = world.AddMob(
            60_000,
            SurfaceQuery.SurfacePosition(terrain, -2.5f, 0.5f),
            primary: ItemType.Sks);

        RunUntilSurface(
            world,
            mob,
            success: actor => actor.Position.X >= rampLength + 0.5f
                              && actor.Position.Y >= plateau - 0.5f,
            maximumSeconds: 180,
            out uint successTick,
            out int jumpTicks,
            out int editsWithWrongTool);

        output.WriteLine(
            $"slope {degrees:0} degrees; final {mob.Position}; success tick {successTick}; "
          + $"jumps {jumpTicks}; terrain edits {terrain.EditVersion}; "
          + $"edits without shovel {editsWithWrongTool}");
        Assert.NotEqual(0u, successTick);
        Assert.Equal(0, editsWithWrongTool);
        Assert.True(terrain.EditVersion > 0, "NPC crossed without excavating the rejected slope");
        Assert.True(jumpTicks < 2 * NetworkConfig.TickRate, $"spent {jumpTicks} ticks jumping at the slope");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void NpcExcavatesOutOfADeepWidePit()
    {
        var terrain = MobIntegrationTerrain.Pit(depth: 6f, halfWidth: 5.5f);
        using var world = new MobIntegrationHarness(terrain, seed: 0xD33);
        world.Flags.Spawn(new Vector3(0.5f, MobIntegrationTerrain.Ground, 18.5f));
        var mob = world.AddMob(
            60_000,
            SurfaceQuery.SurfacePosition(terrain, 0.5f, 0.5f),
            primary: ItemType.Sks);

        RunUntilSurface(
            world,
            mob,
            success: actor => actor.Position.Y >= MobIntegrationTerrain.Ground - 0.35f,
            maximumSeconds: 240,
            out uint successTick,
            out int jumpTicks,
            out int editsWithWrongTool);

        output.WriteLine(
            $"deep pit final {mob.Position}; success tick {successTick}; "
          + $"jumps {jumpTicks}; terrain edits {terrain.EditVersion}; "
          + $"edits without shovel {editsWithWrongTool}");
        Assert.NotEqual(0u, successTick);
        Assert.Equal(0, editsWithWrongTool);
        Assert.True(terrain.EditVersion > 0);
        Assert.False(world.Mobs.TryDequeueStuckMob(out _));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void NpcCrossesAWideTrenchUsingItsOffsetBridge()
    {
        const float trenchHalfWidth = 7.5f;
        const float trenchFloor = 2.5f;
        const float bridgeX = 18.5f;
        const float bridgeHalfWidth = 1.5f;
        var terrain = MobIntegrationTerrain.FromDistance(
            (x, y, z) =>
            {
                float field = y - MobIntegrationTerrain.Ground;
                float trench = MathF.Max(MathF.Abs(z) - trenchHalfWidth, trenchFloor - y);
                field = MathF.Max(field, -trench);
                float bridge = MathF.Max(MathF.Abs(x - bridgeX) - bridgeHalfWidth,
                    y - MobIntegrationTerrain.Ground);
                return MathF.Min(field, bridge);
            },
            chunkRadius: 3);
        using var world = new MobIntegrationHarness(terrain, seed: 0xB71D63);
        world.Flags.Spawn(new Vector3(0.5f, MobIntegrationTerrain.Ground, 20.5f));
        var mob = world.AddMob(
            60_000,
            SurfaceQuery.SurfacePosition(terrain, 0.5f, -20.5f),
            primary: ItemType.Sks);

        RunUntilSurface(
            world,
            mob,
            success: actor => actor.Position.Z >= trenchHalfWidth + 2f
                              && actor.Position.Y >= MobIntegrationTerrain.Ground - 0.5f,
            maximumSeconds: 90,
            out uint successTick,
            out int jumpTicks,
            out int editsWithWrongTool,
            wallClockDelayMs: 3);

        output.WriteLine(
            $"wide trench final {mob.Position}; success tick {successTick}; "
          + $"jumps {jumpTicks}; edits {terrain.EditVersion}; wrong tool {editsWithWrongTool}");
        Assert.NotEqual(0u, successTick);
        Assert.Equal(0, editsWithWrongTool);
        Assert.False(world.Mobs.TryDequeueStuckMob(out _));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void NpcJumpsThroughFoxholeShelvesInsteadOfWalkingAtTheWall()
    {
        var terrain = MobIntegrationTerrain.SoilHeightmap((_, _) => MobIntegrationTerrain.Ground);
        Vector3 origin = SurfaceQuery.SurfacePosition(terrain, 0.5f, 0.5f);
        int setupBites = MobIntegrationTerrain.ExcavateFoxhole(
            terrain,
            origin,
            Vector3.UnitZ,
            MobIntegrationTerrain.Ground);
        long setupVersion = terrain.EditVersion;
        using var world = new MobIntegrationHarness(terrain, seed: 0xF0A);
        world.Flags.Spawn(new Vector3(0.5f, MobIntegrationTerrain.Ground, -12.5f));
        var mob = world.AddMob(
            60_000,
            SurfaceQuery.SurfacePosition(terrain, origin.X, origin.Z),
            primary: ItemType.Sks);
        Assert.True(
            mob.Position.Y <= MobIntegrationTerrain.Ground - 1.25f,
            $"test NPC did not begin physically inside the foxhole: {mob.Position}");

        RunUntilSurface(
            world,
            mob,
            success: actor => actor.Position.Y >= MobIntegrationTerrain.Ground - 0.35f
                              && actor.Position.Z <= -1.5f,
            maximumSeconds: 45,
            out uint successTick,
            out int jumpTicks,
            out int editsWithWrongTool);

        output.WriteLine(
            $"foxhole setup bites {setupBites}; final {mob.Position}; success tick {successTick}; "
          + $"jumps {jumpTicks}; runtime edits {terrain.EditVersion - setupVersion}");
        Assert.NotEqual(0u, successTick);
        Assert.True(jumpTicks > 0, "NPC escaped a jump-height shelf without issuing a jump");
        Assert.Equal(0, editsWithWrongTool);
        Assert.InRange(terrain.EditVersion - setupVersion, 0, 2);
        Assert.False(world.Mobs.TryDequeueStuckMob(out _));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void NpcExcavatesStandingClearanceThroughALowTunnelEntrance()
    {
        var terrain = MobIntegrationTerrain.FromDistance((x, y, z) =>
        {
            float ground = y - MobIntegrationTerrain.Ground;
            float lintel = MathF.Max(
                MathF.Abs(x) - 30f,
                MathF.Max(
                    MathF.Abs(y - 15.5f) - 1.5f,
                    MathF.Abs(z - 1f) - 3f));
            return MathF.Min(ground, lintel);
        });
        using var world = new MobIntegrationHarness(terrain, seed: 0x7A11);
        world.Flags.Spawn(new Vector3(0.5f, MobIntegrationTerrain.Ground, 8.5f));
        var mob = world.AddMob(
            60_000,
            SurfaceQuery.SurfacePosition(terrain, 0.5f, -5.5f),
            primary: ItemType.Sks);

        RunUntilSurface(
            world,
            mob,
            success: actor => actor.Position.Z >= 5.5f
                              && actor.Position.Y <= MobIntegrationTerrain.Ground + 0.75f,
            maximumSeconds: 180,
            out uint successTick,
            out int jumpTicks,
            out int editsWithWrongTool,
            wallClockDelayMs: 1);

        output.WriteLine(
            $"low tunnel final {mob.Position}; success tick {successTick}; "
          + $"jumps {jumpTicks}; edits {terrain.EditVersion}; wrong tool {editsWithWrongTool}");
        Assert.NotEqual(0u, successTick);
        Assert.True(terrain.EditVersion > 0, "NPC crossed the standing-height obstruction without clearing it");
        Assert.Equal(0, editsWithWrongTool);
        Assert.False(world.Mobs.TryDequeueStuckMob(out _));
    }

    private static void RunUntilSurface(
        MobIntegrationHarness world,
        ServerPlayer mob,
        Func<ServerPlayer, bool> success,
        int maximumSeconds,
        out uint successTick,
        out int jumpTicks,
        out int editsWithWrongTool,
        int wallClockDelayMs = 5)
    {
        successTick = 0;
        jumpTicks = 0;
        editsWithWrongTool = 0;
        long observedVersion = world.Terrain.EditVersion;
        var wall = Stopwatch.StartNew();
        uint maximumTicks = (uint)(maximumSeconds * NetworkConfig.TickRate);
        for (uint tick = 0; tick < maximumTicks; tick++)
        {
            world.Step(tick, wallClockDelayMs);
            if (mob.State.HasFlag(PlayerStateFlags.Jumping)) jumpTicks++;
            if (world.Terrain.EditVersion != observedVersion)
            {
                observedVersion = world.Terrain.EditVersion;
                if (mob.Hotbar != HotbarSlot.Shovel) editsWithWrongTool++;
            }
            if (success(mob))
            {
                successTick = tick;
                break;
            }
            if (world.Mobs.TryDequeueStuckMob(out _))
                break;
        }
        wall.Stop();
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find DemiurgeSharp.slnx");
    }
}
