namespace Demiurge.GameServer;

/// <summary>
/// What each NPC decided to do this tick, published for a debug overlay to read.
///
/// Deliberately NOT on the wire. An NPC's intent is a server-side decision that no client needs in
/// order to draw the world, and replicating it would cost every player bandwidth forever so that a
/// developer can occasionally look at it — the same reasoning that keeps <c>SquadRole</c> off the
/// wire. So this is a process-local hand-off instead, which means <c>ai track state</c> shows
/// something only when the client is hosting its own server: singleplayer, <c>session host</c>, or a
/// playtest. Against a remote server it stays empty, and that is the honest answer rather than a
/// missing feature.
///
/// Static for the same reason <see cref="NpcTracker"/> is: the developer terminal is owned by the
/// process and outlives every session, so a debug switch it sets must not have to be plumbed through
/// session construction to be reachable.
///
/// THREADING: singleplayer runs the server on its own thread, so this crosses one. The snapshot is
/// immutable once published and swapped by a single reference write, which is atomic — a reader gets
/// some whole tick's answer, never half of two.
/// </summary>
public static class MobDebugFeed
{
    private static readonly IReadOnlyDictionary<ushort, string> Empty =
        new Dictionary<ushort, string>();

    private static IReadOnlyDictionary<ushort, string> latest = Empty;

    /// <summary>
    /// Whether the server should pay for the snapshot. Set by the overlay toggle: with nobody
    /// looking, publishing is a dictionary and a tick's worth of lookups thrown away 30 times a
    /// second, which is exactly the kind of per-tick cost that is supposed to be earned.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>The most recent tick's labels, by actor id. Never null; empty when nothing publishes.</summary>
    public static IReadOnlyDictionary<ushort, string> Latest => Volatile.Read(ref latest);

    internal static void Publish(IReadOnlyDictionary<ushort, string> states)
        => Volatile.Write(ref latest, states);

    /// <summary>Drops the last snapshot, so a stale one cannot outlive the session that made it.</summary>
    public static void Clear() => Volatile.Write(ref latest, Empty);
}
