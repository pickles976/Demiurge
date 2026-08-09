using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;

namespace Demiurge.ServerTests;

public class CommanderAiTests
{
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
        Assert.True(eastSquad.TryGetObjective(out var eastObjective));
        Assert.Equal(west.NetworkId, westDefence.FlagId);
        Assert.NotEqual(westDefence.FlagId, eastObjective.FlagId);
    }

    private static SquadBlackboard BoardAt(Vector3 centre)
    {
        var board = new SquadBlackboard();
        board.SetRoster([60000], centre);
        return board;
    }
}
