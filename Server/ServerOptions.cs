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
}
