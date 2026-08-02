namespace Demiurge;

/// <summary>Navigation costs expressed in seconds so later walk, fall, and dig edges can compare.</summary>
public static class NavCosts
{
    public const float Inf = 1_000_000f;
    public const float WalkOneMetre = 1f / PlayerMovement.WalkSpeed;
    public const float DiagonalMetres = 1.41421356f;
    public const float MaxSpeed = PlayerMovement.SprintSpeed;

    /// <summary>
    /// One voxel takes two half-second shovel bites. The additional penalty keeps excavation a
    /// fallback behind ordinary movement instead of making a straight tunnel look cheaper than a
    /// modest detour.
    /// </summary>
    public const float DigPenaltyMultiplier = 4f;
    public const float DigExecutionSeconds =
        Digging.ClicksPerVoxel
        * Digging.TicksPerDig
        / (float)NetworkConfig.TickRate;
    public const float DigOneVoxel = DigExecutionSeconds * DigPenaltyMultiplier;
    public const float DigTunnelPenalty = DigExecutionSeconds;

    public static float Fall(float height)
        => height <= 0f ? 0f : MathF.Sqrt(2f * height / PlayerMovement.Gravity);
}
