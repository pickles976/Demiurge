namespace Demiurge.GameServer;

/// <summary>
/// Deterministic startup population for focused scenarios. One spawn on the player's team is
/// reserved for the human; NPCs consume distinct remaining team locations.
/// </summary>
internal sealed record InitialTeamSpawnPlan(
    RuntimePlacement? PlayerSpawn,
    IReadOnlyList<RuntimePlacement> NpcSpawns)
{
    public static InitialTeamSpawnPlan Create(
        IReadOnlyList<RuntimePlacement> placements,
        int? playerTeam,
        int npcsPerTeam)
    {
        if (playerTeam is null || npcsPerTeam == 0)
            return new InitialTeamSpawnPlan(null, []);

        var spawns = placements
            .Where(placement =>
                placement.Kind == RuntimePlacementKind.PlayerSpawn
                && placement.Team > 0)
            .ToArray();
        var playerSpawn = spawns
            .Where(spawn => spawn.Team == playerTeam.Value)
            .OrderBy(spawn =>
                spawn.Position.X * spawn.Position.X
                + spawn.Position.Z * spawn.Position.Z)
            .FirstOrDefault();
        if (playerSpawn.Kind != RuntimePlacementKind.PlayerSpawn)
            throw new InvalidDataException(
                $"Singleplayer scenario has no player spawn for team {playerTeam}");

        var npcSpawns = new List<RuntimePlacement>();
        foreach (int team in spawns.Select(spawn => spawn.Team).Distinct().Order())
        {
            var available = spawns
                .Where(spawn => spawn.Team == team && spawn != playerSpawn)
                .Take(npcsPerTeam)
                .ToArray();
            if (available.Length < npcsPerTeam)
                throw new InvalidDataException(
                    $"Singleplayer scenario needs {npcsPerTeam} NPC spawn locations for team "
                    + $"{team}, but only {available.Length} are available after reserving the player");
            npcSpawns.AddRange(available);
        }

        return new InitialTeamSpawnPlan(playerSpawn, npcSpawns);
    }
}
