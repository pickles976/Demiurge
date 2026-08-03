namespace Demiurge;

/// <summary>Navigation costs expressed in seconds so later walk, fall, and dig edges can compare.</summary>
public static class NavCosts
{
    public const float Inf = 1_000_000f;
    public const float WalkOneMetre = 1f / PlayerMovement.WalkSpeed;
    public const float DiagonalMetres = 1.41421356f;
    public const float MaxSpeed = PlayerMovement.SprintSpeed;

    /// <summary>
    /// The speed every goal heuristic divides straight-line distance by.
    ///
    /// <see cref="PlayerMovement.WalkSpeed"/>, not <see cref="MaxSpeed"/>, and the difference is not
    /// cosmetic: no edge in this graph is ever priced at sprint speed — <see cref="WalkOneMetre"/> is
    /// what every walk edge costs — so dividing by 6 m/s produced a heuristic 33% below the true
    /// cost of the ground it was estimating. A deflated heuristic is still admissible, so routes were
    /// correct, but A* with a loose heuristic behaves like Dijkstra: it spends its expansion budget
    /// sideways instead of forwards, which is expensive here because the budget is 256 expansions and
    /// an expansion costs about 0.8 ms.
    ///
    /// Dividing by walk speed keeps admissibility — straight-line distance times the cheapest
    /// possible per-metre cost is still a lower bound on any real route — while making the estimate
    /// exact on flat open ground. Strictly tighter, same guarantee.
    /// </summary>
    public const float HeuristicSpeed = PlayerMovement.WalkSpeed;

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
