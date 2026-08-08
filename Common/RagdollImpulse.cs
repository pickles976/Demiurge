using System.Numerics;

namespace Demiurge;

/// <summary>
/// How hard, and which way, a killing blow shoves the corpse it leaves behind. Pure maths on plain
/// vectors, so it is testable without an engine and lives on the server, which is the only thing
/// that knows what actually killed anybody. The result travels as
/// <see cref="ImpulseState"/> and the client adds it to the ragdoll's starting velocity.
///
/// It is deliberately unphysical. A real rifle round carries about 4 kg m/s of momentum, which
/// against a 75 kg body is roughly 0.05 m/s — invisible. Bodies are knocked about in games because
/// it reads as force, not because it is what happens, so the numbers below are chosen for how the
/// death looks and are the tuning surface if it looks wrong.
/// </summary>
public static class RagdollImpulse
{
    /// <summary>
    /// Metres per second per point of damage. A rifle body shot (30) shoves at 1.2 m/s — a stagger
    /// and a fall — while a point-blank grenade reaches the cap.
    /// </summary>
    public const float SpeedPerDamage = 0.04f;

    /// <summary>
    /// The ceiling, in m/s. It exists because damage has no upper bound worth trusting — a headshot
    /// doubles it, and blast damage scales off a victim's maximum health — and because an
    /// unbounded shove is the failure this feature had the first time: bodies launched skyward and
    /// span for several seconds. A brisk run, roughly.
    /// </summary>
    public const float MaxSpeed = 4f;

    /// <summary>
    /// The shove a bullet leaves, along the round's line of travel. Direction need not be
    /// normalized; a degenerate one yields no impulse rather than a NaN corpse.
    /// </summary>
    public static Vector3 FromBullet(Vector3 direction, int damage)
        => Along(direction, damage);

    /// <summary>
    /// The shove a blast leaves, pointing from where it went off to the body's centre — so a
    /// grenade at someone's feet lifts them and one beside them throws them sideways, both falling
    /// out of the geometry rather than out of a special case.
    ///
    /// <paramref name="damage"/> is the blow's own magnitude, not the health it happened to remove:
    /// a man on his last hit point who takes a point-blank grenade is thrown by the grenade, not by
    /// the one point of damage that finished him.
    /// </summary>
    public static Vector3 FromBlast(Vector3 origin, Vector3 centre, float damage)
        => Along(centre - origin, damage);

    private static Vector3 Along(Vector3 direction, float damage)
    {
        float length = direction.Length();
        if (!float.IsFinite(length) || length < 1e-6f || !(damage > 0f)) return Vector3.Zero;
        return direction / length * Speed(damage);
    }

    /// <summary>Damage to shove speed: linear, then capped.</summary>
    public static float Speed(float damage)
        => MathF.Min(MathF.Max(damage, 0f) * SpeedPerDamage, MaxSpeed);
}
