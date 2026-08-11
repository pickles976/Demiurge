using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>One waypoint as the overlay draws it: where, and how the actor gets there from the previous one.</summary>
public readonly record struct MobPathPoint(Vector3 Position, NavAction Action);

/// <summary>
/// The route each NPC is currently following, published for the debug overlay to draw.
///
/// A sibling of <see cref="MobDebugFeed"/> in every respect, including the parts that are
/// limitations: not on the wire, so it shows something only when the client hosts its own server
/// (singleplayer, <c>session host</c>, a playtest), and paid for only while somebody is looking.
///
/// It answers the question the intent label cannot. "OBJECTIVE" tells you a man wants to be
/// somewhere; it does not tell you he is walking into a wall, that his route is a two-metre stump
/// because the search ran out of budget, or that the way down off a roof is a fall he was never
/// offered. Those are all shapes you can see in one glance at the line and cannot see any other way.
///
/// THREADING: as <see cref="MobDebugFeed"/> — the snapshot is immutable once published and swapped
/// by one atomic reference write, so a reader gets some whole tick's answer rather than half of two.
/// </summary>
public static class MobPathFeed
{
    private static readonly IReadOnlyDictionary<ushort, IReadOnlyList<MobPathPoint>> Empty =
        new Dictionary<ushort, IReadOnlyList<MobPathPoint>>();

    private static IReadOnlyDictionary<ushort, IReadOnlyList<MobPathPoint>> latest = Empty;

    /// <summary>Whether the server should pay for the snapshot. Set by the overlay toggle.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The most recent tick's routes, by actor id. Never null; empty when nothing publishes.</summary>
    public static IReadOnlyDictionary<ushort, IReadOnlyList<MobPathPoint>> Latest
        => Volatile.Read(ref latest);

    internal static void Publish(IReadOnlyDictionary<ushort, IReadOnlyList<MobPathPoint>> paths)
        => Volatile.Write(ref latest, paths);

    /// <summary>Drops the last snapshot, so a stale one cannot outlive the session that made it.</summary>
    public static void Clear() => Volatile.Write(ref latest, Empty);
}
