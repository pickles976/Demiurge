namespace Demiurge.GameServer;

/// <summary>
/// Low-frequency team commander. It owns only strategic flag assignments; squads still own route,
/// formation, cover, engagement, and capture execution.
/// </summary>
internal sealed class CommanderAi
{
    internal const uint ReplanTicks = NetworkConfig.TickRate;

    private readonly FlagSystem flags;
    private uint nextPlanTick;
    private int plannedSquadCount = -1;

    public CommanderAi(FlagSystem flags) => this.flags = flags;

    public void Update(
        uint tick,
        IReadOnlyDictionary<(int Team, int Squad), SquadBlackboard> squads,
        ICollection<ServerPlayer> actors)
    {
        if (tick < nextPlanTick && squads.Count == plannedSquadCount)
            return;

        nextPlanTick = tick + ReplanTicks;
        plannedSquadCount = squads.Count;

        foreach (var teamGroup in squads
                     .Where(pair => pair.Key.Team > 0)
                     .GroupBy(pair => pair.Key.Team)
                     .OrderBy(group => group.Key))
        {
            int team = teamGroup.Key;
            var boards = teamGroup
                .OrderBy(pair => pair.Key.Squad)
                .ToArray();
            var strategicSquads = boards
                .Select(pair => new StrategicSquad(
                    pair.Key.Squad,
                    pair.Value.Home,
                    pair.Value.TryGetObjective(out var current)
                        ? current.FlagId
                        : 0))
                .ToArray();
            var strategicFlags = flags.StrategicSnapshot(team, actors);
            var assignments = StrategicObjectivePlanner.Plan(
                team,
                strategicSquads,
                strategicFlags);
            var assignedBySquad = assignments.ToDictionary(
                assignment => assignment.SquadId,
                assignment => assignment.FlagId);
            var flagsById = strategicFlags.ToDictionary(flag => flag.FlagId);

            foreach (var pair in boards)
            {
                if (assignedBySquad.TryGetValue(pair.Key.Squad, out uint flagId)
                    && flagsById.TryGetValue(flagId, out var flag))
                {
                    pair.Value.SetObjective(new SquadObjective(flag.FlagId, flag.Position));
                }
                else
                {
                    pair.Value.SetObjective(null);
                }
            }
        }
    }
}
