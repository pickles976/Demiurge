namespace Demiurge;

/// <summary>
/// How big a bang is, as the two radii and the crater it digs. A profile rather than a set of
/// constants because there is now more than one thing that goes off: a grenade and a mortar bomb
/// differ only in scale, and the blast code should take the numbers as an argument rather than
/// reaching for one weapon's globals and being unable to serve the other.
/// </summary>
public readonly record struct BlastProfile(
    float LethalRadius,
    float DamageRadius,
    float TerrainDeformationScale)
{
    /// <summary>Fraction of a victim's maximum health this blast takes at <paramref name="distance"/>:
    /// everything inside the lethal radius, falling linearly to nothing at the damage radius.</summary>
    public float DamageFraction(float distance)
        => distance <= LethalRadius
            ? 1f
            : distance >= DamageRadius
                ? 0f
                : (DamageRadius - distance) / (DamageRadius - LethalRadius);

    /// <summary>The same blast, bigger. Radii and crater together — scaling one without the other
    /// is how a weapon ends up killing over an area it visibly did not touch.</summary>
    public BlastProfile Scaled(float factor)
        => new(LethalRadius * factor, DamageRadius * factor, TerrainDeformationScale * factor);
}

/// <summary>
/// Shared placeholder-grenade tuning. A quick launch and stronger grenade-only gravity produce a
/// low, readable arc while still letting ballistics—not an invisible range wall—limit a perfect
/// 45-degree throw to 40 m.
/// </summary>
public static class GrenadeConfig
{
    public const float MaxThrowRange = 40f;
    public const float ThrowSpeed = 22f;
    public const float Gravity = ThrowSpeed * ThrowSpeed / MaxThrowRange;

    public const float Radius = 0.075f;
    public const float FuseSeconds = 3f;
    public const int FuseTicks = (int)(FuseSeconds * NetworkConfig.TickRate);
    public const float ReloadSeconds = 1.5f;
    public const int ReloadTicks = (int)(ReloadSeconds * NetworkConfig.TickRate);
    public const float TerrainDeformationScale = 0.5f;

    public const float LethalRadius = 4f;
    public const float DamageRadius = 10f;

    /// <summary>Runaway guard for a grenade thrown beyond the loaded world.</summary>
    public const float MaxFlightSeconds = 10f;

    // The normal component loses most of its energy while the tangent retains enough momentum for
    // a short, predictable skip. Floors are deliberately duller than walls.
    //
    // Both were cut by 30% (0.32 and 0.45 before), which is a change to the BOUNCE only:
    // TangentialRetention is left alone because it governs the skid along a surface, not the
    // rebound off it, and scaling it too would have made grenades stop dead rather than bounce less.
    public const float GroundRestitution = 0.224f;
    public const float WallRestitution = 0.315f;
    public const float TangentialRetention = 0.76f;
    public const float GroundNormalThreshold = 0.55f;
    public const float RestSpeed = 0.85f;
    public const float RestNormalSpeed = 0.65f;
    public const float SurfaceOffset = 0.01f;
    public const int MaxImpactsPerTick = 3;

    /// <summary>What a grenade does, as a profile the blast code can be handed.</summary>
    public static BlastProfile Blast => new(LethalRadius, DamageRadius, TerrainDeformationScale);

    public static float DamageFraction(float distance) => Blast.DamageFraction(distance);
}
