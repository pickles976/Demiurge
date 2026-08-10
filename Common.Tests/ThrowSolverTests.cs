using System.Numerics;

namespace Demiurge.Tests;

public class ThrowSolverTests
{
    [Fact]
    public void LowArcReachesRequestedPoint()
    {
        var origin = new Vector3(0f, 1.5f, 0f);
        var target = new Vector3(20f, 0f, 0f);

        Assert.True(ThrowSolver.TryLowArc(
            origin,
            target,
            GrenadeConfig.ThrowSpeed,
            GrenadeConfig.Gravity,
            out var solution));

        Vector3 landing = ThrowSolver.PositionAt(
            origin,
            solution.Direction,
            GrenadeConfig.ThrowSpeed,
            GrenadeConfig.Gravity,
            solution.FlightSeconds);
        Assert.InRange(Vector3.Distance(landing, target), 0f, 0.001f);
        Assert.True(solution.Direction.Y > 0f);
    }

    [Fact]
    public void SolverRejectsTargetsBeyondBallisticReach()
    {
        Assert.False(ThrowSolver.TryLowArc(
            Vector3.Zero,
            new Vector3(GrenadeConfig.MaxThrowRange + 1f, 0f, 0f),
            GrenadeConfig.ThrowSpeed,
            GrenadeConfig.Gravity,
            out _));
    }
}
