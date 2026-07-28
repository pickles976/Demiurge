using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class ServerCommandServiceTests
{
    [Fact]
    public void DisabledServerRejectsMutation()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, new Vector3(10, 0, 20));
        var service = new ServerCommandService(world, allowCheats: false);

        var result = Execute(service, 1, "spawn mob");

        Assert.False(result.Success);
        Assert.Empty(world.SpawnedMobs);
    }

    [Fact]
    public void SpawnMobResolvesRelativePositionOnServerSurface()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, new Vector3(10, 0, 20));
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "spawn mob ~2 ~-3");

        Assert.True(result.Success);
        Assert.Equal(new Vector3(12, 42, 17), Assert.Single(world.SpawnedMobs));
        Assert.Contains("@60000", result.Output);
    }

    [Fact]
    public void SpawnPickupUsesCanonicalItemType()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "spawn pickup ak-47 4 8");

        Assert.True(result.Success);
        var pickup = Assert.Single(world.SpawnedPickups);
        Assert.Equal(ItemType.Ak47, pickup.Item);
        Assert.Equal(new Vector3(4, 42, 8), pickup.Position);
        Assert.Contains("demiurge:ak47", result.Output);
    }

    [Fact]
    public void EquipTargetsMobsAndReusesItemSystemEntryPoint()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        world.AddActor(60000, new Vector3(5, 0, 5), isMob: true);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "equip @60000 demiurge:glock");

        Assert.True(result.Success);
        Assert.Equal((ushort)60000, world.EquippedActor);
        Assert.Equal(ItemType.Glock, world.EquippedItem);
    }

    [Fact]
    public void EquipRejectsMissingActor()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "equip @60000 glock");

        Assert.False(result.Success);
        Assert.Contains("does not exist", result.Output);
    }

    [Fact]
    public void ConsoleIsTrustedButRequiresAbsoluteSpawnCoordinates()
    {
        var world = new FakeCommandWorld();
        var service = new ServerCommandService(world, allowCheats: false);

        var missing = service.ExecuteConsole("spawn mob");
        var relative = service.ExecuteConsole("spawn mob ~2 3");
        var absolute = service.ExecuteConsole("spawn mob 2 3");

        Assert.False(missing.Success);
        Assert.False(relative.Success);
        Assert.True(absolute.Success);
        Assert.Equal(new Vector3(2, 42, 3), Assert.Single(world.SpawnedMobs));
    }

    [Fact]
    public void ConsoleRejectsSelfButCanEquipExplicitActor()
    {
        var world = new FakeCommandWorld();
        world.AddActor(60000, Vector3.Zero, isMob: true);
        var service = new ServerCommandService(world, allowCheats: false);

        Assert.False(service.ExecuteConsole("equip @s glock").Success);
        Assert.True(service.ExecuteConsole("equip @60000 glock").Success);
        Assert.Equal((ushort)60000, world.EquippedActor);
    }

    [Fact]
    public void DedicatedConsoleHelpExplainsSpawnGrammarAndItems()
    {
        string root = DedicatedServerConsole.HelpText([]);
        string spawn = DedicatedServerConsole.HelpText(["spawn"]);
        string pickup = DedicatedServerConsole.HelpText(["spawn", "pickup"]);
        string items = DedicatedServerConsole.HelpText(["items"]);

        Assert.Contains("spawn pickup <item> <x> <z>", root);
        Assert.Contains("spawn mob 10 -15", spawn);
        Assert.Contains("spawn pickup ak47 0 0", pickup);
        Assert.Contains("demiurge:ak47", items);
        Assert.Contains("aliases: ak47", items);
    }

    private static CommandResultData Execute(
        ServerCommandService service,
        ushort issuer,
        string command)
        => service.Execute(issuer, new CommandRequestData { RequestId = 7, Command = command });

    private sealed class FakeCommandWorld : ICommandWorld
    {
        private readonly Dictionary<ushort, ServerPlayer> actors = new();
        private ushort nextMobId = 60000;
        private uint nextObjectId = 1;

        public List<Vector3> SpawnedMobs { get; } = new();
        public List<(ItemType Item, Vector3 Position)> SpawnedPickups { get; } = new();
        public ushort? EquippedActor { get; private set; }
        public ItemType? EquippedItem { get; private set; }

        public void AddActor(ushort id, Vector3 position, bool isMob = false)
            => actors[id] = new ServerPlayer
            {
                Id = id,
                IsMob = isMob,
                Move = new MoveState { Position = position },
            };

        public ServerPlayer SpawnMob(Vector3? requestedPosition = null)
        {
            var position = requestedPosition ?? Vector3.Zero;
            SpawnedMobs.Add(position);
            var mob = new ServerPlayer
            {
                Id = nextMobId++,
                IsMob = true,
                Move = new MoveState { Position = position },
            };
            actors[mob.Id] = mob;
            return mob;
        }

        public ServerObject SpawnPickup(ItemType type, Vector3 position)
        {
            SpawnedPickups.Add((type, position));
            return new ServerObject { NetworkId = nextObjectId++, Type = ObjectType.Item };
        }

        public bool TryGetActor(ushort actorId, out ServerPlayer actor)
            => actors.TryGetValue(actorId, out actor!);

        public ServerObject Equip(ServerPlayer actor, ItemType type)
        {
            EquippedActor = actor.Id;
            EquippedItem = type;
            return new ServerObject { NetworkId = nextObjectId++, Type = ObjectType.Item };
        }

        public bool IsSpawnableColumn(float worldX, float worldZ)
            => MathF.Abs(worldX) < 500 && MathF.Abs(worldZ) < 500;

        public Vector3 SurfacePosition(float worldX, float worldZ)
            => new(worldX, 42, worldZ);
    }
}
