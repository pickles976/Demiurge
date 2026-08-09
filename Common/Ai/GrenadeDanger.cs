using System.Numerics;

namespace Demiurge;

/// <summary>One grenade in the world, as the AI needs to see it.</summary>
/// <param name="Seconds">Until it goes off. Zero or negative means it is going off now.</param>
public readonly record struct LiveBlast(Vector3 Position, float Seconds, BlastProfile Blast);

/// <summary>
/// How much trouble a man is in from live grenades, and which way is out.
///
/// Risk is the fraction of a body the blast takes, straight from
/// <see cref="BlastProfile.DamageFraction"/> — the same function the server uses to hurt him, so an
/// NPC cannot be afraid of a grenade that would not have hurt it, or calm about one that would.
///
/// Time is part of the score rather than a separate gate. Running costs cover and takes seconds; a
/// fuse with no time left cannot be outrun, so the honest response to it is to stay put rather than
/// to sprint into the open and be caught standing.
/// </summary>
public static class GrenadeDanger
{
    /// <summary>How fast a man gets clear, for deciding whether running is worth it. Walk speed
    /// rather than sprint: he is diving away from his feet, not setting off on a journey.</summary>
    public static float EscapeSpeed => PlayerMovement.WalkSpeed;

    /// <summary>
    /// The worst fraction of a body the live blasts will take at <paramref name="position"/>,
    /// discounted by whether there is time to get out of it, and the direction to go.
    ///
    /// Zero means stay where you are — either nothing is close enough, or nothing can be escaped.
    /// </summary>
    public static float Evaluate(
        Vector3 position,
        IReadOnlyList<LiveBlast> blasts,
        out Vector3 away)
    {
        away = Vector3.Zero;
        float worst = 0f;

        for (int i = 0; i < blasts.Count; i++)
        {
            var blast = blasts[i];
            var delta = position - blast.Position;
            delta.Y = 0f;
            float distance = delta.Length();

            float fraction = blast.Blast.DamageFraction(distance);
            if (fraction <= 0f) continue;

            // How much of the way out he can cover before it goes off. No time, no point running.
            float reachable = MathF.Max(0f, blast.Seconds) * EscapeSpeed;
            float escapable = blast.Blast.DamageRadius - distance;
            float feasibility = escapable <= 0f
                ? 0f
                : Math.Clamp(reachable / escapable, 0f, 1f);

            float risk = fraction * feasibility;
            if (risk <= 0f) continue;

            worst = MathF.Max(worst, risk);

            // Weighted by risk and summed, so two grenades either side push him out sideways rather
            // than cancelling to a stand. A man exactly between two is the case that has to produce
            // an answer, not a zero.
            var escape = distance > 1e-3f ? delta / distance : Vector3.UnitX;
            away += escape * risk;
        }

        if (away.LengthSquared() > 1e-6f)
            away = Vector3.Normalize(away);
        else if (worst > 0f)
            // Perfectly balanced: any direction beats standing on it.
            away = Vector3.UnitX;

        return worst;
    }
}
