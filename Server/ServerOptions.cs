using System.Numerics;

namespace Demiurge.GameServer;

public sealed record ServerOptions
{
    public bool AllowCheats { get; init; }
    public string? MapPath { get; init; }
    public RuntimeMap? RuntimeMap { get; init; }

    /// <summary>
    /// Feet position that replaces the map's player spawns for this server. The editor playtest
    /// sets it to the fly camera's exact position, so play starts where you were looking; a normal
    /// server leaves it null and the map's spawns apply.
    /// </summary>
    public Vector3? SpawnOverride { get; init; }

    /// <summary>
    /// Optional team for the first connecting player. Used by focused single-player scenarios;
    /// subsequent connections still use normal team balancing.
    /// </summary>
    public int? InitialPlayerTeam { get; init; }

    /// <summary>
    /// Optional scenario population built from the map's team player-spawn locations. Zero leaves
    /// authored mob placements entirely in control. Large scenarios spread additional NPCs around
    /// the authored team locations when the requested count exceeds the number of markers.
    /// </summary>
    public int InitialNpcsPerTeam { get; init; }
}
