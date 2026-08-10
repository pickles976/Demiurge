using System.Numerics;

namespace Demiurge;

/// <summary>One remembered gunshot, and how much it deserves attention.</summary>
public readonly record struct HeardShot(
    ushort ShooterId,
    Vector3 Position,
    uint Tick,
    float Salience);

/// <summary>
/// Short-term memory of enemy gunfire, ranked by salience rather than by recency.
///
/// It replaces a single last-write-wins slot on MobBrain, which had the specific failure that a
/// distant shot erased a point-blank one — so an NPC forgot the player standing behind it because
/// somebody fired across the map on the next tick. A shot fired two metres away is not the same
/// event as one fired fifty metres away, and recency cannot express the difference.
///
/// Small and fixed-size: this is a handful of entries per NPC, kept for a few seconds, and there are
/// 32 NPCs. One entry per shooter, because a man firing a magazine is one contact, not thirty.
/// </summary>
public sealed class HeardShots
{
    /// <summary>Enough to hold every shooter an NPC can realistically be tracking at once. Beyond
    /// this the quietest is dropped, which is the correct thing to forget.</summary>
    public const int Capacity = 4;

    private readonly HeardShot[] shots = new HeardShot[Capacity];
    private int count;

    public int Count => count;

    /// <summary>Loudness of a shot at this distance: 1 in your ear, 0 at the edge of hearing.</summary>
    public static float SalienceAt(float distance)
    {
        float t = Math.Clamp(distance / GunshotHearing.MaximumDistance, 0f, 1f);
        return (1f - t) * (1f - t);
    }

    public void Hear(ushort shooterId, Vector3 perceived, float distance, uint tick)
    {
        var shot = new HeardShot(shooterId, perceived, tick, SalienceAt(distance));

        for (int i = 0; i < count; i++)
        {
            if (shots[i].ShooterId != shooterId) continue;
            // Same man firing again: refresh rather than accumulate. A magazine is one contact.
            shots[i] = shot;
            return;
        }

        if (count < Capacity)
        {
            shots[count++] = shot;
            return;
        }

        int quietest = 0;
        for (int i = 1; i < count; i++)
            if (shots[i].Salience < shots[quietest].Salience) quietest = i;

        if (shot.Salience > shots[quietest].Salience) shots[quietest] = shot;
    }

    /// <summary>The shot most worth reacting to, ignoring any that have gone stale.</summary>
    public bool TryMostSalient(uint tick, out HeardShot shot)
    {
        shot = default;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            if (Expired(shots[i], tick)) continue;
            if (found && shots[i].Salience <= shot.Salience) continue;

            shot = shots[i];
            found = true;
        }

        return found;
    }

    public void Prune(uint tick)
    {
        int kept = 0;
        for (int i = 0; i < count; i++)
            if (!Expired(shots[i], tick))
                shots[kept++] = shots[i];

        for (int i = kept; i < count; i++) shots[i] = default;
        count = kept;
    }

    public void Clear()
    {
        Array.Clear(shots);
        count = 0;
    }

    private static bool Expired(in HeardShot shot, uint tick)
        => tick >= shot.Tick && tick - shot.Tick >= GunshotHearing.InvestigationTicks;
}
