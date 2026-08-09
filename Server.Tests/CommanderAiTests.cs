using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

public class CommanderAiTests
{
    [Fact]
    public void CommanderAssignsOneNpcToAnAdvantageousWeapon()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var flags = new FlagSystem(objects);
        var items = new ItemSystem(objects);
        var actor = MobAt(60000, Vector3.Zero);
        items.SpawnInfantryLoadout(actor, ItemType.Ppsh);
        var dp27 = items.SpawnPickup(ItemType.Dp27, new Vector3(2f, 0f, 0f));
        var board = BoardAt(Vector3.Zero);
        board.Publish(new AiContact(7, new Vector3(80f, 0f, 0f), 1, 1f), 1);
        board.Advance(100);
        var squads = new Dictionary<(int Team, int Squad), SquadBlackboard>
        {
            [(1, 0)] = board,
        };

        new CommanderAi(flags, objects).Update(100, squads, [actor]);

        Assert.True(board.TryGetResourceObjective(actor.Id, out var objective));
        Assert.Equal(SquadResourceKind.AcquireWeapon, objective.Kind);
        Assert.Equal(dp27.NetworkId, objective.ObjectId);
    }

    [Fact]
    public void CommanderAssignsOneOperatorToAMortarCoveringTheThreat()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var flags = new FlagSystem(objects);
        var items = new ItemSystem(objects);
        var actor = MobAt(60000, Vector3.Zero);
        items.SpawnInfantryLoadout(actor, ItemType.Sks);
        var mortar = items.SpawnPickup(ItemType.Mortar, Vector3.Zero);
        mortar.Transform.Yaw = 0f;
        var board = BoardAt(Vector3.Zero);
        board.Publish(new AiContact(7, new Vector3(0f, 0f, 100f), 1, 1f), 1);
        board.Advance(100);
        var squads = new Dictionary<(int Team, int Squad), SquadBlackboard>
        {
            [(1, 0)] = board,
        };

        new CommanderAi(flags, objects).Update(100, squads, [actor]);

        Assert.True(board.TryGetResourceObjective(actor.Id, out var objective));
        Assert.Equal(SquadResourceKind.OperateMortar, objective.Kind);
        Assert.Equal(mortar.NetworkId, objective.ObjectId);
    }

    [Fact]
    public void OneWeaponPickupIsReservedForOneNpc()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var flags = new FlagSystem(objects);
        var items = new ItemSystem(objects);
        var first = MobAt(60000, Vector3.Zero);
        var second = MobAt(60001, new Vector3(0f, 0f, 2f));
        items.SpawnInfantryLoadout(first, ItemType.Ppsh);
        items.SpawnInfantryLoadout(second, ItemType.Ppsh);
        items.SpawnPickup(ItemType.Dp27, new Vector3(2f, 0f, 0f));
        var firstBoard = BoardAt(first.Position, first.Id);
        var secondBoard = BoardAt(second.Position, second.Id);
        var squads = new Dictionary<(int Team, int Squad), SquadBlackboard>
        {
            [(1, 0)] = firstBoard,
            [(1, 1)] = secondBoard,
        };

        new CommanderAi(flags, objects).Update(1, squads, [first, second]);

        int assignments = 0;
        if (firstBoard.TryGetResourceObjective(first.Id, out _)) assignments++;
        if (secondBoard.TryGetResourceObjective(second.Id, out _)) assignments++;
        Assert.Equal(1, assignments);
    }

    [Fact]
    public void CommanderDoesNotShellFriendliesNearTheContact()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var flags = new FlagSystem(objects);
        var items = new ItemSystem(objects);
        var gunner = MobAt(60000, Vector3.Zero);
        var friendly = MobAt(60001, new Vector3(0f, 0f, 60f));
        items.SpawnInfantryLoadout(gunner, ItemType.Sks);
        var mortar = items.SpawnPickup(ItemType.Mortar, Vector3.Zero);
        mortar.Transform.Yaw = 0f;
        var board = BoardAt(Vector3.Zero);
        board.Publish(new AiContact(7, friendly.Position, 1, 1f), 1);
        board.Advance(100);
        var squads = new Dictionary<(int Team, int Squad), SquadBlackboard>
        {
            [(1, 0)] = board,
        };

        new CommanderAi(flags, objects).Update(100, squads, [gunner, friendly]);

        Assert.False(board.TryGetResourceObjective(gunner.Id, out _));
    }

    [Fact]
    /// <summary>
    /// Rewritten 2026-08-09. The second half used to assert that the east squad ABANDONED its own
    /// uncontested objective to double up on the threatened western flag. That is the mass-
    /// concentration loop that was reported — with no enemy actually present, a second squad adds
    /// nothing to the defence, while the flag it walked away from was free.
    ///
    /// The property that survives is the one that is about doctrine rather than about the old
    /// ladder: a threatened flag is defended by the squad that owns it, and the commander spreads
    /// rather than stacking. See StrategicValueTests for the marginal-value rule underneath.
    /// </summary>
    public void CommanderDistributesAndKeepsAThreatenedFlagDefended()
    {
        var objects = new ObjectReplication(new NullNetServer());
        var flags = new FlagSystem(objects);
        var west = flags.Spawn(new Vector3(-20f, 0f, 0f));
        var east = flags.Spawn(new Vector3(20f, 0f, 0f));
        var westSquad = BoardAt(new Vector3(-25f, 0f, 0f));
        var eastSquad = BoardAt(new Vector3(25f, 0f, 0f));
        var squads = new Dictionary<(int Team, int Squad), SquadBlackboard>
        {
            [(1, 0)] = westSquad,
            [(1, 1)] = eastSquad,
        };
        var commander = new CommanderAi(flags, objects);

        commander.Update(tick: 1, squads, actors: []);

        Assert.True(westSquad.TryGetObjective(out var westOpening));
        Assert.True(eastSquad.TryGetObjective(out var eastOpening));
        Assert.Equal(west.NetworkId, westOpening.FlagId);
        Assert.Equal(east.NetworkId, eastOpening.FlagId);

        west.Team.Value = 1;
        west.Team.CapturingTeam = 2;
        west.Team.Progress = 0.5f;
        commander.Update(
            tick: 1 + CommanderAi.ReplanTicks,
            squads,
            actors: []);

        Assert.True(westSquad.TryGetObjective(out var westDefence));
        Assert.True(eastSquad.TryGetObjective(out var eastObjective));
        Assert.Equal(west.NetworkId, westDefence.FlagId);
        Assert.NotEqual(westDefence.FlagId, eastObjective.FlagId);
    }

    private static SquadBlackboard BoardAt(Vector3 centre, ushort actorId = 60000)
    {
        var board = new SquadBlackboard();
        board.SetRoster([actorId], centre);
        return board;
    }

    private static ServerPlayer MobAt(ushort id, Vector3 position)
        => new()
        {
            Id = id,
            IsMob = true,
            Team = 1,
            Move = new MoveState { Position = position },
            Status = new ServerObject
            {
                Has = NetComponents.Health,
                Health = new HealthState { Current = 100, Max = 100 },
            },
        };
}
