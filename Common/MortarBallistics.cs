using System.Numerics;

namespace Demiurge;

/// <summary>
/// Where a mortar can drop a bomb, and how fast it has to throw one to get it there. Pure maths on
/// plain vectors, shared because both ends need the same answers: the client draws the sector and
/// previews the arc, the server decides whether a request is legal and then flies the round.
///
/// A mortar is aimed at a POINT, and that is the whole difference from every other weapon here. A
/// rifle takes a direction and the world decides where it stops; a mortar takes a place and the
/// arithmetic decides the direction. Everything below follows from that inversion.
/// </summary>
public static class MortarBallistics
{
    /// <summary>
    /// How steeply the bomb leaves the tube. Fixed, with the SPEED solved per shot, which is the
    /// choice that makes a mortar a mortar: the arc always looks the same and the range is bought
    /// with charge rather than by flattening the trajectory into a rifle shot. Steep enough to drop
    /// behind cover, short of vertical so the flight time stays bearable at minimum range.
    /// </summary>
    public const float LaunchAngleDegrees = 70f;

    public static float LaunchAngleRadians => LaunchAngleDegrees * MathF.PI / 180f;

    /// <summary>Where the bomb leaves the tube, above the emplacement's own position.</summary>
    public const float MuzzleHeight = 0.9f;

    /// <summary>
    /// The point this mortar will actually drop a bomb on, given where the gunner asked for. It is a
    /// CLAMP rather than a yes-or-no because the cursor is a continuous thing sliding around a
    /// bounded sector: refusing an illegal point would make the aim marker vanish at the edges,
    /// where a gunner spends most of his time. Sliding it to the nearest legal point keeps the
    /// marker under the cursor's direction and honest about the limit at the same time.
    ///
    /// <paramref name="facingYaw"/> is the heading the tube was emplaced on — see ItemSystem.PutDown,
    /// which records where its owner was looking as he set it down.
    /// </summary>
    public static Vector3 ClampTarget(Vector3 mortar, float facingYaw, Vector3 desired)
    {
        var flat = new Vector3(desired.X - mortar.X, 0f, desired.Z - mortar.Z);
        float range = flat.Length();

        // Straight over the tube: no bearing to preserve, so lay it on the emplaced heading at
        // minimum range rather than dividing by zero.
        var facing = new Vector3(MathF.Sin(facingYaw), 0f, MathF.Cos(facingYaw));
        if (range < 1e-3f)
            return (mortar + facing * MortarConfig.MinimumRange) with { Y = desired.Y };

        var bearing = flat / range;

        // Worked in YAW rather than as a cross-and-dot angle, because yaw is the convention the
        // emplacement is stored in: a heading is atan2(x, z) here, and converting to a signed angle
        // and back is where the sense of the rotation gets flipped.
        float bearingYaw = MathF.Atan2(bearing.X, bearing.Z);
        float relative = NormalizeAngle(bearingYaw - facingYaw);

        float limit = MortarConfig.SectorHalfAngleDegrees * MathF.PI / 180f;
        float clampedAngle = Math.Clamp(relative, -limit, limit);
        float clampedRange = Math.Clamp(range, MortarConfig.MinimumRange, MortarConfig.MaximumRange);

        float aimYaw = facingYaw + clampedAngle;
        var aim = new Vector3(MathF.Sin(aimYaw), 0f, MathF.Cos(aimYaw));
        var clamped = mortar + aim * clampedRange;
        return clamped with { Y = desired.Y };
    }

    /// <summary>Whether a point is one this mortar could have been asked for — the server's check
    /// that a fire request was not fabricated. Generous by a small margin, because the client
    /// clamps against ITS copy of the emplacement and a rounded float should not cost a shot.</summary>
    public static bool IsLegalTarget(Vector3 mortar, float facingYaw, Vector3 target)
    {
        var clamped = ClampTarget(mortar, facingYaw, target);
        var flatClamped = clamped with { Y = 0f };
        var flatTarget = target with { Y = 0f };
        return Vector3.DistanceSquared(flatClamped, flatTarget) <= LegalTargetTolerance * LegalTargetTolerance;
    }

    /// <summary>Metres of slack between a claimed target and the nearest legal one.</summary>
    public const float LegalTargetTolerance = 1f;

    /// <summary>An angle folded into [-pi, pi], so a bearing either side of the heading compares
    /// against the sector limit rather than against its complement.</summary>
    public static float NormalizeAngle(float radians)
    {
        const float twoPi = 2f * MathF.PI;
        radians %= twoPi;
        if (radians > MathF.PI) radians -= twoPi;
        if (radians < -MathF.PI) radians += twoPi;
        return radians;
    }

    /// <summary>
    /// Where a bomb aimed at <paramref name="target"/> actually comes down: a gaussian scatter
    /// across the ground at <see cref="MortarConfig.DispersionMetres"/>.
    ///
    /// Sampled on the SERVER and never predicted. A client that could work out where its own bomb
    /// would land could aim off and cancel the scatter, which would turn an area weapon back into a
    /// sniper rifle with a five-second reload.
    /// </summary>
    public static Vector3 Disperse(Vector3 target, Random random)
    {
        float angle = (float)(random.NextDouble() * 2.0 * Math.PI);
        float distance = (float)Gaussian(random) * MortarConfig.DispersionMetres;
        return target + new Vector3(distance * MathF.Cos(angle), 0f, distance * MathF.Sin(angle));
    }

    /// <summary>Box-Muller: a uniform pair in, one standard normal out.</summary>
    private static double Gaussian(Random random)
        => Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble()))
           * Math.Cos(2.0 * Math.PI * random.NextDouble());

    /// <summary>
    /// The velocity to leave the tube with so the bomb lands on <paramref name="target"/>, at the
    /// fixed <see cref="LaunchAngleDegrees"/>, under <paramref name="gravity"/>.
    ///
    /// From the standard projectile relation, solved for speed rather than angle:
    /// <c>v² = g·d² / (2·cos²θ·(d·tanθ − h))</c>, where d is the horizontal distance and h the rise
    /// to the target. Null when the target sits so far above the tube that a 70-degree throw cannot
    /// reach it however hard it is thrown — <c>d·tanθ ≤ h</c>, the denominator going non-positive.
    /// That is a real case (a mortar at the foot of a cliff) and it has to be refused rather than
    /// producing a NaN round.
    /// </summary>
    public static Vector3? SolveVelocity(Vector3 muzzle, Vector3 target, float gravity)
    {
        var flat = new Vector3(target.X - muzzle.X, 0f, target.Z - muzzle.Z);
        float distance = flat.Length();
        if (distance < 1e-3f || gravity <= 0f) return null;

        float rise = target.Y - muzzle.Y;
        float angle = LaunchAngleRadians;
        float cos = MathF.Cos(angle);
        float denominator = 2f * cos * cos * (distance * MathF.Tan(angle) - rise);
        if (denominator <= 0f) return null;

        float speedSquared = gravity * distance * distance / denominator;
        if (!float.IsFinite(speedSquared) || speedSquared <= 0f) return null;

        float speed = MathF.Sqrt(speedSquared);
        var direction = flat / distance;
        return direction * (speed * cos) + Vector3.UnitY * (speed * MathF.Sin(angle));
    }
}
