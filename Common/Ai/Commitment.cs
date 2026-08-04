namespace Demiurge;

/// <summary>
/// A decision that has to survive being reconsidered.
///
/// Plans here are recomputed far faster than they can be carried out — squad tactics replan at 2 Hz,
/// squads re-form at 1 Hz, and a bound takes several seconds — so any decision stored as a bare value
/// gets silently overwritten by the next pass. That failure has now happened three times in this
/// codebase, each time looking like a different bug:
///
/// <list type="bullet">
/// <item>Bearings were dealt out in sort order every replan, so a mover was redirected to the far
/// side of the threat twice a second. Measured, 37 bounds produced 13 m of displacement per man
/// against a 4 m/s walk speed — they spent the fight turning around.</item>
/// <item>Squad membership was reassigned by proximity mid-manoeuvre, changing a man's bearing and
/// bound index under him.</item>
/// <item>An excavation site was re-picked from scratch after every shovel bite, because each bite
/// changed the terrain and invalidated the path that chose it.</item>
/// </list>
///
/// Each was patched separately with a private field and a convention that the next reader had to
/// remember. This makes "is this still mine, and is it still valid?" a property of the value, so
/// there is nothing to remember and nothing to forget.
///
/// Deliberately not a lease or a lock: nobody else is contending for it. It is one actor's decision
/// and the only question is whether it has outlived its usefulness.
/// </summary>
public readonly record struct Commitment<T>(T Value, uint MadeAtTick, uint ExpiresAtTick)
    where T : struct
{
    /// <summary>Nothing committed. Distinguishable from a committed default value, which is why this
    /// is not just `default(T)`.</summary>
    public static Commitment<T> None => default;

    public bool Exists => ExpiresAtTick != 0;

    public bool IsLive(uint tick) => Exists && tick < ExpiresAtTick;

    public static Commitment<T> For(T value, uint tick, uint ticks)
        => new(value, tick, tick + Math.Max(1u, ticks));

    /// <summary>The committed value if it still stands, otherwise nothing — so a caller cannot read a
    /// stale commitment by forgetting to check.</summary>
    public bool TryGet(uint tick, out T value)
    {
        value = Value;
        return IsLive(tick);
    }

    /// <summary>
    /// Keeps an existing live commitment, or makes a fresh one. This is the whole usage pattern: a
    /// replan calls it every time and the commitment only moves when it has genuinely lapsed.
    /// </summary>
    public Commitment<T> Renew(T value, uint tick, uint ticks)
        => IsLive(tick) ? this : For(value, tick, ticks);

    public Commitment<T> Released() => None;
}
