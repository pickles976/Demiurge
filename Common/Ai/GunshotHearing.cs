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
}
