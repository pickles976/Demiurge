using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Detects an actor that is expected to travel but makes no meaningful positional or terrain
/// progress. Deliberate stationary states reset the watch, so defending, aiming, and reloading are
/// never mistaken for navigation failure.
/// </summary>
internal sealed class NavigationProgressWatch
{
    internal const uint StuckTicks = 60 * NetworkConfig.TickRate;
    private const float MeaningfulDistance = 2f;
    private const float MeaningfulDistanceSquared =
        MeaningfulDistance * MeaningfulDistance;

    private bool active;
    private Vector3 anchor;
    private uint sinceTick;

    public bool Update(
        Vector3 position,
        uint tick,
        bool expectedToTravel,
        bool terrainProgress = false)
    {
        if (!expectedToTravel)
        {
            Reset();
            return false;
        }

        if (!active
            || terrainProgress
            || HorizontalDistanceSquared(position, anchor) >= MeaningfulDistanceSquared)
        {
            active = true;
            anchor = position;
            sinceTick = tick;
            return false;
        }

        if (tick - sinceTick < StuckTicks)
            return false;

        Reset();
        return true;
    }

    public void Reset()
    {
        active = false;
        anchor = default;
        sinceTick = 0;
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
