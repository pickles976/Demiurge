using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

/// <summary>
/// Where a bomb goes off. The conquest map spans 23 m of elevation between its flags, so "the
/// ground" is not one height and a mortar that assumes it is bursts in the air over everything
/// downhill of the tube.
/// </summary>
public class MortarImpactTests
{
    /// <summary>Ground height at <paramref name="x"/>: a smooth slope from 40 m down to 10 m, so the
    /// target is well over a blast radius below the tube.</summary>
    private static float GroundAt(float x) => 25f - x * 0.375f;

    private const float TubeX = -30f;
    private const float TargetX = 30f;

    [Fact]
    [Trait("Category", "Integration")]
    public void ABombAimedDownhillDetonatesOnTheGroundRatherThanAtTheTubesElevation()
    {
        var server = new NullNetServer();
        var terrain = SlopedTerrain();
        var objects = new ObjectReplication(server);
        var terrainEdits = new TerrainSystem(server, terrain);
        var mortars = new MortarSystem(objects, terrain, terrainEdits, dispersionSeed: 0xA11CE);
        var items = new ItemSystem(objects);

        var tubePosition = new Vector3(TubeX, GroundAt(TubeX), 0f);
        var gunner = new ServerPlayer
        {
            Id = 1,
            Move = new MoveState { Position = tubePosition },
        };
        var mortar = items.SpawnPickup(ItemType.Mortar, tubePosition);
        // Laid down the slope, toward +X.
        mortar.Transform.Yaw = MathF.PI / 2f;

        // Standing on the ground where the bomb is aimed, which is 22.5 m below the tube.
        var victim = new ServerPlayer
        {
            Id = 2,
            Move = new MoveState { Position = new Vector3(TargetX, GroundAt(TargetX), 0f) },
            Status = new ServerObject
            {
                Has = NetComponents.Health,
                Health = new HealthState { Current = 100, Max = 100 },
            },
        };

        // The client aims by mapping the cursor onto a FLAT plane at the tube's own elevation, so
        // this is the Y a real request carries. The server must not take its word for it.
        var request = new Vector3(TargetX, tubePosition.Y, 0f);

        Assert.True(mortars.TryFire(gunner, mortar, request, tick: 0));

        var actors = new[] { gunner, victim };
        for (uint tick = 1; tick <= 30 * 30 && mortars.InFlight > 0; tick++)
            mortars.Tick(NetworkConfig.FixedDt, tick, actors);

        Assert.Equal(0, mortars.InFlight);
        Assert.True(
            victim.Status!.Health.Current < 100,
            "a bomb aimed at a man standing below the tube must reach him, not burst at the tube's height");
    }

    private static ChunkMap SlopedTerrain()
    {
        var map = new ChunkMap();
        for (int cz = -2; cz <= 2; cz++)
            for (int cx = -4; cx <= 4; cx++)
            {
                var index = new ChunkIndex { x = cx, z = cz };
                var chunk = new TerrainChunk(index);
                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    var column = ChunkTransforms.ColumnWorldPosition(
                        index,
                        ChunkTransforms.ColumnIndexOf(i));
                    int worldY = ChunkConstants.WorldMinY + ChunkTransforms.LocalYOf(i);
                    float distance = ChunkConstants.ClampToWorldFloor(
                        worldY,
                        worldY - GroundAt(column.X));

                    var voxel = new Voxel { Distance = distance };
                    voxel.Material = ChunkGenerator.DensityToMaterial(voxel.Distance, distance);
                    chunk[i] = voxel;
                }
                map.Insert(chunk);
            }
        return map;
    }
}
