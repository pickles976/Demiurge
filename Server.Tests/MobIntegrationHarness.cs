using System.Numerics;
using Demiurge.GameServer;
using Riptide;

namespace Demiurge.ServerTests;

/// <summary>Real server-system fixture for the deliberately opt-in NPC integration scenarios.</summary>
internal sealed class MobIntegrationHarness : IDisposable
{
    public MobIntegrationHarness(ChunkMap terrain, int seed)
    {
        Terrain = terrain;
        Server = new Server();
        Objects = new ObjectReplication(Server);
        Items = new ItemSystem(Objects);
        TerrainEdits = new TerrainSystem(Server, terrain);
        Weapons = new WeaponSystem(Server, Objects, terrain);
        Flags = new FlagSystem(Objects);
        Grenades = new GrenadeSystem(Objects, Items, TerrainEdits, terrain);
        Mobs = new MobSystem(terrain, TerrainEdits, Weapons, Flags, Grenades, seed);
    }

    public ChunkMap Terrain { get; }
    public Server Server { get; }
    public ObjectReplication Objects { get; }
    public ItemSystem Items { get; }
    public TerrainSystem TerrainEdits { get; }
    public WeaponSystem Weapons { get; }
    public FlagSystem Flags { get; }
    public GrenadeSystem Grenades { get; }
    public MobSystem Mobs { get; }
    public List<ServerPlayer> Actors { get; } = [];

    public ServerPlayer AddMob(
        ushort id,
        Vector3 position,
        int team = 1,
        ItemType primary = ItemType.Ppsh)
    {
        var mob = Mobs.CreateMob(id, position, team);
        mob.Status = Status();
        Items.SpawnInfantryLoadout(mob, primary);
        Actors.Add(mob);
        return mob;
    }

    public ServerPlayer AddEnemy(ushort id, Vector3 position, int team = 2)
    {
        var enemy = new ServerPlayer
        {
            Id = id,
            Team = team,
            Move = PlayerMovement.SpawnAt(Terrain, position.X, position.Z),
            Status = Status(),
        };
        Actors.Add(enemy);
        return enemy;
    }

    public void Step(uint tick, int wallClockDelayMs = 5)
    {
        Mobs.BeginTick(tick, Actors);
        foreach (var actor in Actors)
            if (actor.IsMob && actor.Status is { Health.Current: > 0 })
                Mobs.Step(actor, NetworkConfig.FixedDt, tick, Actors);
        if (wallClockDelayMs > 0)
            Thread.Sleep(wallClockDelayMs);
    }

    public void Relocate(ServerPlayer mob, Vector3 position)
    {
        mob.Move = PlayerMovement.SpawnAt(Terrain, position.X, position.Z);
        mob.History.Clear();
        mob.PendingMoves.Clear();
        mob.LastIntent = Vector3.Zero;
        mob.State = 0;
        Mobs.OnRespawn(mob);
    }

    public void Dispose() => Mobs.Dispose();

    private static ServerObject Status() => new()
    {
        Type = ObjectType.PlayerStatus,
        Has = NetComponents.Health,
        Health = new HealthState { Current = 100, Max = 100 },
    };
}

internal static class MobIntegrationTerrain
{
    public const float Ground = 12.5f;

    public static ChunkMap SoilHeightmap(
        Func<int, int, float> height,
        int chunkRadius = 2)
        => Build(
            (x, y, z) => y - height(x, z),
            chunkRadius);

    public static ChunkMap Pit(float depth, float halfWidth, int chunkRadius = 2)
    {
        float floor = Ground - depth;
        return Build(
            (x, y, z) =>
            {
                float field = y - Ground;
                float pit = MathF.Max(
                    MathF.Max(MathF.Abs(x) - halfWidth, MathF.Abs(z) - halfWidth),
                    floor - y);
                return MathF.Max(field, -pit);
            },
            chunkRadius);
    }

    public static ChunkMap FromDistance(
        Func<int, int, int, float> distance,
        int chunkRadius = 2)
        => Build(distance, chunkRadius);

    public static int ExcavateFoxhole(
        ChunkMap map,
        Vector3 origin,
        Vector3 toward,
        float grade)
    {
        int bites = 0;
        while (FoxholePlan.NextBite(map, origin, toward, grade) is { } target)
        {
            TerrainEdits.ApplyBox(
                map,
                target,
                Digging.Bite,
                EditMode.SubtractSoil,
                BlockType.BlockType_Air,
                EditShape.Sphere,
                Digging.BiteStrength);
            if (++bites > 512)
                throw new InvalidOperationException("foxhole plan did not converge");
        }
        return bites;
    }

    private static ChunkMap Build(
        Func<int, int, int, float> distance,
        int chunkRadius)
    {
        var map = new ChunkMap();
        for (int cz = -chunkRadius; cz <= chunkRadius; cz++)
            for (int cx = -chunkRadius; cx <= chunkRadius; cx++)
            {
                var index = new ChunkIndex { x = cx, z = cz };
                var chunk = new TerrainChunk(index);
                (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);
                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    (int lx, int ly, int lz) = ChunkTransforms.LocalVoxelCoords(i);
                    int y = ChunkConstants.WorldMinY + ly;
                    float value = ChunkConstants.ClampToWorldFloor(
                        y,
                        distance(originX + lx, y, originZ + lz));
                    var voxel = new Voxel { Distance = value };
                    voxel.Material = value < 0f
                        ? BlockType.BlockType_Dirt
                        : BlockType.BlockType_Air;
                    chunk[i] = voxel;
                }
                map.Insert(chunk);
            }
        return map;
    }
}
