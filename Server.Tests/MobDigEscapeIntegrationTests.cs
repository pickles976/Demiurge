using System.Diagnostics;
using System.Numerics;
using Demiurge.GameServer;
using Riptide;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// End-to-end escape coverage for the real NPC loop. The older Common DigEscapeTests deliberately
/// teleports its synthetic actor onto each newly standable tread; that proves search geometry but
/// cannot prove that MobSystem, PathFollower, PlayerMovement, shovel cadence, asynchronous replans,
/// and terrain invalidation work together.
/// </summary>
public sealed class MobDigEscapeIntegrationTests(ITestOutputHelper output)
{
    private const float Ground = 12.5f;

    [Fact]
    [Trait("Category", "Integration")]
    public void NpcExcavatesOutOfAJumpProofPitAndReturnsToSurface()
    {
        var terrain = Pit();
        Vector3 bottom = SurfaceQuery.SurfacePosition(terrain, 0.5f, 0.5f);
        Assert.True(bottom.Y < Ground - 2f, $"test pit was only {Ground - bottom.Y:0.00} m deep");

        var server = new Server();
        var objects = new ObjectReplication(server);
        var items = new ItemSystem(objects);
        var terrainEdits = new TerrainSystem(server, terrain);
        var weapons = new WeaponSystem(server, objects, terrain);
        var flags = new FlagSystem(objects);
        var grenades = new GrenadeSystem(objects, items, terrainEdits, terrain);
        flags.Spawn(new Vector3(0.5f, Ground, 14.5f));

        using var mobs = new MobSystem(
            terrain,
            terrainEdits,
            weapons,
            flags,
            grenades,
            seed: 0xD16);
        var mob = mobs.CreateMob(60_000, bottom, team: 1);
        mob.Status = new ServerObject
        {
            Type = ObjectType.PlayerStatus,
            Has = NetComponents.Health,
            Health = new HealthState { Current = 100, Max = 100 },
        };
        items.SpawnInfantryLoadout(mob);
        var actors = new List<ServerPlayer> { mob };

        const uint maximumTicks = 90 * NetworkConfig.TickRate;
        long startingTerrainVersion = terrain.EditVersion;
        var wallClock = Stopwatch.StartNew();
        uint escapedTick = 0;
        uint stuckTick = 0;
        float maximumY = mob.Position.Y;
        int jumpTicks = 0;
        for (uint tick = 0; tick < maximumTicks; tick++)
        {
            mobs.BeginTick(tick, actors);
            mobs.Step(mob, NetworkConfig.FixedDt, tick, actors);
            maximumY = MathF.Max(maximumY, mob.Position.Y);
            if (mob.State.HasFlag(PlayerStateFlags.Jumping)) jumpTicks++;

            // Run game time about six times faster than real time while still yielding enough wall
            // clock for the asynchronous navigation workers to answer. Their search budgets are
            // wall-clock based, so a 30x tick loop would make one ordinary 100 ms search consume
            // three simulated seconds and test scheduler starvation rather than NPC escape.
            // No movement or terrain shortcut is taken: this is the same BeginTick/Step loop
            // GameWorld drives.
            Thread.Sleep(5);

            if (mob.Position.Y >= Ground - 0.35f)
            {
                escapedTick = tick;
                break;
            }
            if (mobs.TryDequeueStuckMob(out _))
            {
                stuckTick = tick;
                break;
            }
        }

        wallClock.Stop();
        output.WriteLine(
            $"start {bottom}; end {mob.Position}; escaped tick {escapedTick}/{maximumTicks}; "
          + $"stuck tick {stuckTick}; "
          + $"terrain edits {terrain.EditVersion - startingTerrainVersion}; "
          + $"max Y {maximumY:0.00}; jump ticks {jumpTicks}; "
          + $"wall {wallClock.Elapsed.TotalSeconds:0.00}s");

        Assert.NotEqual(0u, escapedTick);
        Assert.Equal(0u, stuckTick);
        Assert.True(
            terrain.EditVersion > startingTerrainVersion,
            "NPC reached the surface without exercising its shovel path");
    }

    private static ChunkMap Pit()
    {
        const float floor = Ground - 2.5f;
        const float halfWidth = 1.5f;
        var map = new ChunkMap();
        for (int cz = -1; cz <= 1; cz++)
            for (int cx = -1; cx <= 1; cx++)
            {
                var index = new ChunkIndex { x = cx, z = cz };
                var chunk = new TerrainChunk(index);
                (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);
                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    (int lx, int ly, int lz) = ChunkTransforms.LocalVoxelCoords(i);
                    int y = ChunkConstants.WorldMinY + ly;
                    int x = originX + lx;
                    int z = originZ + lz;
                    float field = y - Ground;
                    float pit = MathF.Max(
                        MathF.Max(MathF.Abs(x) - halfWidth, MathF.Abs(z) - halfWidth),
                        floor - y);
                    float distance = ChunkConstants.ClampToWorldFloor(
                        y,
                        MathF.Max(field, -pit));
                    var voxel = new Voxel { Distance = distance };
                    voxel.Material = distance < 0f
                        ? BlockType.BlockType_Dirt
                        : BlockType.BlockType_Air;
                    chunk[i] = voxel;
                }
                map.Insert(chunk);
            }
        return map;
    }
}
