using System.Diagnostics;
using System.Numerics;
using Demiurge.GameServer;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

[Collection(AiIntegrationCollection.Name)]
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
        foreach (var flag in map.Placements.Where(p => p.Kind == RuntimePlacementKind.ConquestFlag))
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

        // Dense team-two spawns deliberately stabilize a partial-recovery assignment before the
        // outer members clear the authored base lip. Twenty seconds made the assertion hinge on a
        // final 0.2 m under concurrent test load; thirty still catches a stranded spawn while
        // allowing the bounded successor request to complete and execute.
        for (uint tick = 0; tick < 30 * NetworkConfig.TickRate; tick++)
            // Navigation is deliberately off-thread; leave enough real wall time for the bounded
            // recovery queue so the accelerated harness does not simulate thirty seconds while
            // workers have received only a couple of seconds of CPU.
            world.Step(tick, wallClockDelayMs: 6);

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

    /// <summary>
    /// Team 1's base wall can trap bounded searches in alternating short prefixes. Require every
    /// actor to leave the basin so a successful nearest squad cannot hide the failure.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void TeamOneEscapesSpawnAndCapturesItsNearestFlag()
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(
            Path.Combine(root, "maps", "conquest", "runtime.dmap"));
        var plan = InitialTeamSpawnPlan.Create(
            map.Placements,
            playerTeam: 1,
            npcsPerTeam: 16);
        var teamSpawns = plan.NpcSpawns.Where(spawn => spawn.Team == 1).ToArray();
        Assert.Equal(16, teamSpawns.Length);
        Vector3 spawnCentre = new(
            teamSpawns.Average(spawn => spawn.Position.X),
            teamSpawns.Average(spawn => spawn.Position.Y),
            teamSpawns.Average(spawn => spawn.Position.Z));

        using var world = new MobIntegrationHarness(map.Terrain, seed: 0x71A1);
        var flags = map.Placements
            .Where(placement => placement.Kind == RuntimePlacementKind.ConquestFlag)
            .Select(placement => (Placement: placement, Object: world.Flags.Spawn(placement.Position)))
            .ToArray();
        var nearest = flags.MinBy(flag => HorizontalDistanceSquared(spawnCentre, flag.Placement.Position));

        ushort id = 60_000;
        var starts = new Dictionary<ushort, Vector3>();
        foreach (var spawn in teamSpawns)
        {
            Vector3 position = NavTraversal.TryFindNearestStandableForActor(
                    map.Terrain,
                    spawn.Position,
                    horizontalRadius: 8,
                    out var spawnCell)
                ? NavTraversal.Position(map.Terrain, spawnCell)
                : spawn.Position;
            var mob = world.AddMob(id++, position, team: 1);
            starts[mob.Id] = mob.Position;
        }

        uint capturedTick = 0;
        var maximumDisplacement = starts.Keys.ToDictionary(actorId => actorId, _ => 0f);
        var stuckEvents = new List<(ushort ActorId, float Displacement)>();
        uint maximumTicks = 180u * NetworkConfig.TickRate;
        for (uint tick = 0; tick < maximumTicks; tick++)
        {
            world.Step(tick, wallClockDelayMs: 2);
            foreach (var mob in world.Actors.Where(actor => actor.IsMob && actor.Team == 1))
            {
                maximumDisplacement[mob.Id] = MathF.Max(
                    maximumDisplacement[mob.Id],
                    MathF.Sqrt(HorizontalDistanceSquared(starts[mob.Id], mob.Position)));
            }
            while (world.Mobs.TryDequeueStuckMob(out ushort stuckId))
                stuckEvents.Add((stuckId, maximumDisplacement[stuckId]));
            if (capturedTick == 0
                && nearest.Object.Team.Value == 1
                && nearest.Object.Team.Progress >= 0.999f)
                capturedTick = tick;
        }

        output.WriteLine(
            $"team 1 spawn {spawnCentre}; nearest flag {nearest.Placement.Position}; "
          + $"captured tick {capturedTick}/{maximumTicks}; path requests {world.Mobs.DebugPathRequests}; "
          + $"stuck {string.Join(',', stuckEvents.Select(item => $"{item.ActorId}@{item.Displacement:0}m"))}");
        var flagsById = flags.ToDictionary(flag => flag.Object.NetworkId);
        foreach (var assignment in world.Mobs.DebugAssignments().OrderBy(item => item.ActorId))
        {
            var mob = world.Actors.Single(actor => actor.Id == assignment.ActorId);
            output.WriteLine(
                $"mob {mob.Id} squad {assignment.Squad} flag {assignment.FlagId}: "
              + $"{starts[mob.Id]} -> {mob.Position}; max {maximumDisplacement[mob.Id]:0.0} m; "
              + $"destination {assignment.Destination}");
            Assert.True(
                maximumDisplacement[mob.Id] >= 75f,
                $"mob {mob.Id} never escaped Team 1's spawn-wall basin");
            Assert.True(flagsById.TryGetValue(assignment.FlagId, out var assignedFlag));
            Assert.True(
                HorizontalDistanceSquared(assignment.Destination, assignedFlag.Placement.Position)
                    <= 60f * 60f,
                $"mob {mob.Id} retained destination {assignment.Destination} from another squad "
              + $"after reassignment to flag {assignment.FlagId} at {assignedFlag.Placement.Position}");
        }
        Assert.DoesNotContain(stuckEvents, item => item.Displacement < 75f);
        Assert.NotEqual(0u, capturedTick);
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(1)]
    [InlineData(2)]
    public void EachConquestTeamCapturesBothCentralFlagsWithoutStuckRelocation(int team)
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(
            Path.Combine(root, "maps", "conquest", "runtime.dmap"));
        var plan = InitialTeamSpawnPlan.Create(
            map.Placements,
            playerTeam: 1,
            npcsPerTeam: 16);
        var flags = map.Placements
            .Where(placement => placement.Kind == RuntimePlacementKind.ConquestFlag)
            .ToArray();
        // The live conquest map has two home flags and three central objectives. This scenario
        // deliberately exercises the two objectives nearest map centre; its old four-flag shape
        // assertion prevented either team from ever entering the simulation after the fifth flag
        // was added.
        Assert.Equal(5, flags.Length);
        using var world = new MobIntegrationHarness(map.Terrain, seed: 0xD17C + team);
        var centralFlags = flags
            .Select(flag => (Placement: flag, Object: world.Flags.Spawn(flag.Position)))
            .OrderBy(flag => flag.Placement.Position.X * flag.Placement.Position.X
                           + flag.Placement.Position.Z * flag.Placement.Position.Z)
            .Take(2)
            .ToArray();

        ushort id = 60_000;
        foreach (var spawn in plan.NpcSpawns.Where(spawn => spawn.Team == team))
        {
            Vector3 position = NavTraversal.TryFindNearestStandable(
                    map.Terrain,
                    spawn.Position,
                    horizontalRadius: 8,
                    out var spawnCell)
                ? NavTraversal.Position(map.Terrain, spawnCell)
                : spawn.Position;
            _ = world.AddMob(id++, position, spawn.Team);
        }
        Assert.Equal(16, world.Actors.Count(actor => actor.IsMob && actor.Team == team));

        var captured = new bool[centralFlags.Length];
        var capturedBy = new int[centralFlags.Length];
        var stuckEvents = new Dictionary<ushort, (uint Tick, int Team, Vector3 Position)>();
        var stuckNavigation = new Dictionary<ushort, string>();
        uint completedTick = 0;
        const uint maximumTicks = 300 * NetworkConfig.TickRate;
        for (uint tick = 0; tick < maximumTicks; tick++)
        {
            world.Step(tick, wallClockDelayMs: 2);
            // Match GameWorld ordering, but fail instead of relocating so teleportation cannot satisfy it.
            while (world.Mobs.TryDequeueStuckMob(out ushort stuckMobId))
            {
                var stuck = world.Actors.Single(actor => actor.Id == stuckMobId);
                stuckEvents.TryAdd(stuckMobId, (tick, stuck.Team, stuck.Position));
                stuckNavigation.TryAdd(stuckMobId, world.Mobs.DebugNavigation(stuckMobId));
            }
            world.Flags.Tick(NetworkConfig.FixedDt, world.Actors);

            for (int flagIndex = 0; flagIndex < centralFlags.Length; flagIndex++)
            {
                ref var state = ref centralFlags[flagIndex].Object.Team;
                if (state.Value == FlagConfig.NeutralTeam || state.Progress < 0.999f)
                    continue;
                captured[flagIndex] = true;
                capturedBy[flagIndex] = state.Value;
            }

            if (!captured.All(value => value)) continue;
            completedTick = tick;
            break;
        }

        for (int flagIndex = 0; flagIndex < centralFlags.Length; flagIndex++)
            output.WriteLine(
                $"central flag {centralFlags[flagIndex].Placement.Position}: "
              + $"live position {centralFlags[flagIndex].Object.Transform.Position}, "
              + $"captured {captured[flagIndex]} by team {capturedBy[flagIndex]}, "
              + $"final owner {centralFlags[flagIndex].Object.Team.Value}, "
              + $"progress {centralFlags[flagIndex].Object.Team.Progress:0.00}");
        output.WriteLine(
            $"team {team} captured both central flags at tick {completedTick}/{maximumTicks}; "
          + $"terrain edits {map.Terrain.EditVersion}");
        var navMetrics = world.Mobs.DebugNavigationMetrics;
        output.WriteLine(
            $"nav: requested {navMetrics.Requested} completed {navMetrics.Completed} "
          + $"complete {navMetrics.CompletePaths} partial {navMetrics.PartialPaths} "
          + $"cancelled {navMetrics.Cancelled} spatialInvalidations {navMetrics.SpatialInvalidations} "
          + $"spatialTrims {navMetrics.SpatialTrims} startChunk {navMetrics.StartChunkInvalidations} "
          + $"sharedReuses {navMetrics.SharedRouteReuses}");
        foreach (var (mobId, stuck) in stuckEvents)
            output.WriteLine(
                $"STUCK team {stuck.Team} mob {mobId} at tick {stuck.Tick}: {stuck.Position}; "
              + stuckNavigation[mobId]);

        Assert.Empty(stuckEvents);
        Assert.All(
            Enumerable.Range(0, centralFlags.Length),
            flagIndex => Assert.True(
                captured[flagIndex],
                $"central flag {centralFlags[flagIndex].Placement.Position} was never captured; "
              + $"final owner {centralFlags[flagIndex].Object.Team.Value}, "
              + $"progress {centralFlags[flagIndex].Object.Team.Progress:0.00}"));
        Assert.NotEqual(0u, completedTick);
        Assert.InRange(
            map.Terrain.EditVersion,
            0,
            // Five flags send the far-side team across one additional authored escarpment. The
            // squad excavation lease keeps this below two planned half-bites per NPC-minute; the
            // old uncoordinated run made 135 edits and occasionally relocated a stuck digger.
            96);
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
        float minimumY = mob.Position.Y;

        RunUntilSurface(
            world,
            mob,
            success: actor =>
            {
                minimumY = MathF.Min(minimumY, actor.Position.Y);
                return actor.Position.Z >= trenchHalfWidth + 2f
                       && actor.Position.Y >= MobIntegrationTerrain.Ground - 0.5f;
            },
            maximumSeconds: 90,
            out uint successTick,
            out int jumpTicks,
            out int editsWithWrongTool,
            wallClockDelayMs: 3);

        output.WriteLine(
            $"wide trench final {mob.Position}; success tick {successTick}; "
          + $"minimum Y {minimumY:0.00}; jumps {jumpTicks}; "
          + $"edits {terrain.EditVersion}; wrong tool {editsWithWrongTool}");
        Assert.NotEqual(0u, successTick);
        Assert.True(
            minimumY > MobIntegrationTerrain.Ground - 2f,
            $"NPC descended into the trench instead of taking the bridge; minimum Y was {minimumY:0.00}");
        Assert.Equal(0, terrain.EditVersion);
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
