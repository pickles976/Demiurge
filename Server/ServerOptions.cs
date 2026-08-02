using System.Numerics;
using Demiurge.Net;

namespace Demiurge.GameServer;

public sealed record ServerOptions
{
    /// <summary>
    /// Transport this server listens on. Null means real UDP via Riptide, which is what a dedicated
    /// server and a normal host both want.
    /// </summary>
    /// <remarks>
    /// This is the runtime selection point, and it has to be runtime rather than compile time because
    /// one binary must both host singleplayer and <c>session join</c> a remote server. Singleplayer
    /// passes the server end of an <see cref="InProcessNetwork"/> pair here and hands the client end to
    /// <c>NetworkManager</c>; nothing else in the server knows the difference.
    /// </remarks>
    public INetServer? Transport { get; init; }

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
