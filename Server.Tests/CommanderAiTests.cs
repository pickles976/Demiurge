using System.Numerics;
using Demiurge.GameServer;
using Riptide;

namespace Demiurge.ServerTests;

public class CommanderAiTests
{
    [Fact]
    public void CommanderDistributesThenReinforcesAThreatenedFriendlyFlag()
    {
        var objects = new ObjectReplication(new Server());
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
        var commander = new CommanderAi(flags);

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
        Assert.True(eastSquad.TryGetObjective(out var eastReinforcement));
        Assert.Equal(west.NetworkId, westDefence.FlagId);
        Assert.Equal(west.NetworkId, eastReinforcement.FlagId);
    }

    private static SquadBlackboard BoardAt(Vector3 centre)
    {
        var board = new SquadBlackboard();
        board.SetRoster([60000], centre);
        return board;
    }
}
