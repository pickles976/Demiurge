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
        Assert.Contains("actor ID @60000", result.Output);
    }

    [Fact]
    public void SpawnPickupUsesCanonicalItemType()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "spawn pickup sks 4 8");

        Assert.True(result.Success);
        var pickup = Assert.Single(world.SpawnedPickups);
        Assert.Equal(ItemType.Sks, pickup.Item);
        Assert.Equal(new Vector3(4, 42, 8), pickup.Position);
        Assert.Contains("demiurge:sks", result.Output);
        Assert.Contains("object ID #1", result.Output);
    }

    [Fact]
    public void EquipTargetsMobsAndReusesItemSystemEntryPoint()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        world.AddActor(60000, new Vector3(5, 0, 5), isMob: true);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "equip @60000 demiurge:ppsh");

        Assert.True(result.Success);
        Assert.Equal((ushort)60000, world.EquippedActor);
        Assert.Equal(ItemType.Ppsh, world.EquippedItem);
    }

    [Fact]
    public void EquipRejectsMissingActor()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "equip @60000 ppsh");

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

        Assert.False(service.ExecuteConsole("equip @s ppsh").Success);
        Assert.True(service.ExecuteConsole("equip @60000 ppsh").Success);
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
        Assert.Contains("ai stats", root);
        Assert.Contains("spawn mob 10 -15", spawn);
        Assert.Contains("spawn pickup sks 0 0", pickup);
        Assert.Contains("demiurge:sks", items);
        Assert.Contains("aliases: sks", items);
    }

    [Fact]
    public void AiStatsUsesExistingCommandResultChannel()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "ai stats");

        Assert.True(result.Success);
        Assert.Equal("test AI stats", result.Output);
    }

    private static CommandResultData Execute(
        ServerCommandService service,
        ushort issuer,
        string command)
        => service.Execute(issuer, new CommandRequestData { RequestId = 7, Command = command });

    [Fact]
    public void TeamCommandMovesTheNamedActor()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        world.AddActor(60000, new Vector3(5, 0, 5), isMob: true);
        var service = new ServerCommandService(world, allowCheats: true);

        Assert.True(Execute(service, 1, "team @60000 2").Success);

        Assert.True(world.TryGetActor(60000, out var mob));
        Assert.Equal(2, mob.Team);
    }

    /// <summary>@s is the issuer, so a player can put himself on the other side without knowing his
    /// own actor id.</summary>
    [Fact]
    public void TeamCommandAcceptsSelf()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        Assert.True(Execute(service, 1, "team @s 2").Success);

        Assert.True(world.TryGetActor(1, out var player));
        Assert.Equal(2, player.Team);
    }

    /// <summary>
    /// A team the map does not have is refused by the WORLD rather than by the grammar, and the
    /// failure has to reach the issuer: which sides exist is a property of what is loaded, so a
    /// parser that knew them would be a parser that goes stale when the map changes.
    /// </summary>
    [Fact]
    public void TeamCommandRefusesASideTheMapDoesNotHave()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        var result = Execute(service, 1, "team @s 9");

        Assert.False(result.Success);
        Assert.Contains("not playable", result.Output);
        Assert.True(world.TryGetActor(1, out var player));
        Assert.Equal(1, player.Team);
    }

    [Fact]
    public void TeamCommandRejectsAnActorThatDoesNotExist()
    {
        var world = new FakeCommandWorld();
        world.AddActor(1, Vector3.Zero);
        var service = new ServerCommandService(world, allowCheats: true);

        Assert.False(Execute(service, 1, "team @60123 2").Success);
    }

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

        /// <summary>Two playable sides, which is what the real world's map placements usually
        /// amount to; anything else is rejected so the "not playable" path is reachable here.</summary>
        public bool TrySetTeam(ServerPlayer actor, int team, out string message)
        {
            if (team is not (1 or 2))
            {
                message = $"Team {team} is not playable on this map";
                return false;
            }
            if (actor.Team == team)
            {
                message = $"@{actor.Id} is already on team {team}";
                return false;
            }
            actor.Team = team;
            message = $"Moved @{actor.Id} to team {team}";
            return true;
        }

        public List<ushort> Killed { get; } = new();

        public bool TryKill(ServerPlayer actor, out string message)
        {
            Killed.Add(actor.Id);
            message = $"Killed @{actor.Id}";
            return true;
        }

        public bool IsSpawnableColumn(float worldX, float worldZ)
            => MathF.Abs(worldX) < 500 && MathF.Abs(worldZ) < 500;

        public Vector3 SurfacePosition(float worldX, float worldZ)
            => new(worldX, 42, worldZ);

        public string AiStats() => "test AI stats";
    }
}
