using System.Numerics;

namespace Demiurge;

/// <summary>
/// Which pickup a player's E key would take, given where they are standing.
///
/// This exists in Common because two people ask the question and they must not disagree: the server
/// asks it to decide what to equip, and the client asks it to decide what to offer. A prompt that
/// says "Press E to pick up DP-27" when the server would take the rifle lying behind it is worse
/// than no prompt at all, and the way that happens is two copies of "nearest item in radius"
/// drifting apart. Same reasoning as WeaponMount holding both the weapon seat and the muzzle: one
/// source, two readers.
///
/// The client's copy is advisory — the server still decides, and it decides from its own positions,
/// so a prompt can still be a frame stale near the edge of the radius. What it cannot be is wrong
/// about the RULE.
/// </summary>
public static class PickupTargeting
{
    /// <summary>
    /// How close a player must stand, in metres. Reach, not a search radius: it is how far an arm
    /// goes, so it stays short enough that which item you mean is never ambiguous.
    /// </summary>
    public const float Radius = 1.5f;

    public const float RadiusSquared = Radius * Radius;

    /// <summary>
    /// How far above or below the thing a man may stand and still reach it.
    ///
    /// <see cref="Radius"/> is horizontal, which on flat ground is the whole of reach and on a map
    /// with trenches and parapets is not: a man on a lip ten metres over a mortar was within 1.5 m
    /// of it by that measure and could work the tube from up there. Reach is a sphere, not a
    /// cylinder, and this is the half-height of it — about a man, so standing on a sandbag still
    /// counts and standing on a roof does not.
    /// </summary>
    public const float VerticalReach = 1.8f;

    /// <summary>
    /// The facts about an object this decision needs, so that each side can project its own
    /// representation — the server's ServerObject, the client's NetObject — into one shape rather
    /// than this file knowing about either of them.
    /// </summary>
    public readonly record struct Candidate(NetComponents Has, ItemType Type, Vector3 Position);

    /// <summary>
    /// Whether this object is a thing lying in the world that a player could pick up at all.
    /// Item + Transform is "a pickup rather than an equipped item"; the category test is what keeps
    /// walk-over inventory items, when they exist, from offering an equip that would not happen.
    /// </summary>
    public static bool IsAvailable(in Candidate candidate)
        => candidate.Has.HasFlag(NetComponents.Item | NetComponents.Transform)
           && ItemConfig.IsHeld(candidate.Type);

    /// <summary>
    /// The pickup within reach of <paramref name="from"/>, nearest first, or null for none. The
    /// caller gets its OWN object back rather than a candidate, so it can act on it.
    /// </summary>
    public static T? Nearest<T>(
        Vector3 from,
        IEnumerable<T> objects,
        Func<T, Candidate> describe)
        where T : class
    {
        T? nearest = null;
        float nearestSquared = RadiusSquared;

        foreach (var obj in objects)
        {
            var candidate = describe(obj);
            if (!IsAvailable(candidate)) continue;

            float distanceSquared = Vector3.DistanceSquared(candidate.Position, from);
            if (distanceSquared > nearestSquared) continue;

            nearest = obj;
            nearestSquared = distanceSquared;
        }

        return nearest;
    }
}
