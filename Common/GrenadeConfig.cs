namespace Demiurge;

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
    public const float GroundRestitution = 0.32f;
    public const float WallRestitution = 0.45f;
    public const float TangentialRetention = 0.76f;
    public const float GroundNormalThreshold = 0.55f;
    public const float RestSpeed = 0.85f;
    public const float RestNormalSpeed = 0.65f;
    public const float SurfaceOffset = 0.01f;
    public const int MaxImpactsPerTick = 3;

    public static float DamageFraction(float distance)
        => distance <= LethalRadius
            ? 1f
            : distance >= DamageRadius
                ? 0f
                : (DamageRadius - distance) / (DamageRadius - LethalRadius);
}
