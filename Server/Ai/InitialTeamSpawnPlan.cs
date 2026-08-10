using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Deterministic startup population for focused scenarios. One spawn on the player's team is
/// reserved for the human. Authored team locations seed a compact golden-angle formation when a
/// large battle requests more actors than distinct editor markers.
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
                .ToArray();
            if (available.Length == 0)
                throw new InvalidDataException(
                    $"Singleplayer scenario needs {npcsPerTeam} NPC spawn locations for team "
                    + $"{team}, but none are available after reserving the player");

            var generated = new List<RuntimePlacement>(npcsPerTeam);
            generated.AddRange(available.Take(npcsPerTeam));
            if (generated.Count < npcsPerTeam)
            {
                Vector3 centre = new(
                    available.Average(spawn => spawn.Position.X),
                    available.Average(spawn => spawn.Position.Y),
                    available.Average(spawn => spawn.Position.Z));
                int candidate = 1;
                while (generated.Count < npcsPerTeam)
                {
                    float radius = 1.25f * MathF.Sqrt(candidate);
                    float angle = (candidate + team * 17) * 2.39996323f;
                    Vector3 position = centre + new Vector3(
                        MathF.Cos(angle) * radius,
                        0f,
                        MathF.Sin(angle) * radius);
                    candidate++;
                    if (generated.Any(spawn =>
                        HorizontalDistanceSquared(spawn.Position, position) < 1f))
                        continue;

                    var template = available[generated.Count % available.Length];
                    generated.Add(template with { Position = position });
                }
            }
            npcSpawns.AddRange(generated);
        }

        return new InitialTeamSpawnPlan(playerSpawn, npcSpawns);
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
