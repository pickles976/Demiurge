namespace Demiurge.GameServer;

/// <summary>
/// Team-relative strategic objectives supplied by the active game mode.
///
/// Static control points are represented first. A carried flag or delivery objective can implement
/// the same snapshot contract later without teaching the commander about capture rules or concrete
/// game-mode systems.
/// </summary>
internal interface IGameModeObjectiveProvider
{
    IReadOnlyList<StrategicFlag> StrategicSnapshot(
        int team,
        ICollection<ServerPlayer> actors);
}
