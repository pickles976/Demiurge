namespace Demiurge;

/// <summary>Navigation costs expressed in seconds so later walk, fall, and dig edges can compare.</summary>
public static class NavCosts
{
    public const float Inf = 1_000_000f;
    public const float WalkOneMetre = 1f / PlayerMovement.WalkSpeed;
    public const float DiagonalMetres = 1.41421356f;
    public const float MaxSpeed = PlayerMovement.SprintSpeed;

    public static float Fall(float height)
        => height <= 0f ? 0f : MathF.Sqrt(2f * height / PlayerMovement.Gravity);
}
