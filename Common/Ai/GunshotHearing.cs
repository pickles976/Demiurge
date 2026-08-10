using System.Numerics;

namespace Demiurge;

/// <summary>Cheap team/distance gate for server-authoritative gunshot investigation.</summary>
public static class GunshotHearing
{
    public const float MaximumDistance = 60f;
    public const int InvestigationTicks = 6 * NetworkConfig.TickRate;

    public static bool CanHear(
        int listenerTeam,
        Vector3 listenerPosition,
        int shooterTeam,
        Vector3 shotPosition)
        => listenerTeam > 0
           && shooterTeam > 0
           && listenerTeam != shooterTeam
           && Vector3.DistanceSquared(listenerPosition, shotPosition)
               <= MaximumDistance * MaximumDistance;

    /// <summary>
    /// How far a heard shot's perceived position can sit from the truth, at a given distance.
    ///
    /// Deliberately near-zero up close. A player firing a few metres behind an NPC must be located,
    /// not merely noticed — that specific failure is the reason this exists.
    /// </summary>
    public const float MinimumError = 0.5f;
    public const float MaximumError = 12f;

    public static float LocalisationError(float distance)
    {
        float t = Math.Clamp(distance / MaximumDistance, 0f, 1f);
        return MinimumError + (MaximumError - MinimumError) * t * t;
    }

    /// <summary>
    /// Where the listener thinks the shot came from. Deterministic in <paramref name="seed"/> so a
    /// replay or a test sees the same answer twice; the offset is horizontal because a listener
    /// misjudges bearing and range, not elevation.
    /// </summary>
    public static Vector3 PerceivedPosition(Vector3 listener, Vector3 shot, uint seed)
    {
        float distance = Vector3.Distance(listener, shot);
        float error = LocalisationError(distance);

        uint state = seed == 0 ? 0x9e3779b9u : seed;
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        float angle = state / (float)uint.MaxValue * MathF.Tau;
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        float radius = MathF.Sqrt(state / (float)uint.MaxValue) * error;

        return shot + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
    }
}
