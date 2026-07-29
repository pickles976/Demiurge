using System.Numerics;

namespace Demiurge;

public readonly record struct ThrowSolution(
    Vector3 Direction,
    float FlightSeconds);

/// <summary>Closed-form low ballistic arc used by AI throwables and testable without world state.</summary>
public static class ThrowSolver
{
    public static bool TryLowArc(
        Vector3 origin,
        Vector3 target,
        float speed,
        float gravity,
        out ThrowSolution solution)
    {
        solution = default;
        if (!IsFinite(origin)
            || !IsFinite(target)
            || !float.IsFinite(speed)
            || !float.IsFinite(gravity)
            || speed <= 0f
            || gravity <= 0f)
            return false;

        Vector3 delta = target - origin;
        var horizontal = new Vector3(delta.X, 0f, delta.Z);
        float distance = horizontal.Length();
        if (distance <= 1e-4f) return false;

        float speedSquared = speed * speed;
        float discriminant =
            speedSquared * speedSquared
            - gravity * (gravity * distance * distance + 2f * delta.Y * speedSquared);
        if (discriminant < 0f) return false;

        float tangent =
            (speedSquared - MathF.Sqrt(discriminant))
            / (gravity * distance);
        float cosine = 1f / MathF.Sqrt(1f + tangent * tangent);
        float sine = tangent * cosine;
        if (!float.IsFinite(cosine) || cosine <= 1e-5f || !float.IsFinite(sine))
            return false;

        Vector3 direction = horizontal / distance * cosine + Vector3.UnitY * sine;
        float flightSeconds = distance / (speed * cosine);
        if (!IsFinite(direction)
            || !float.IsFinite(flightSeconds)
            || flightSeconds <= 0f)
            return false;

        solution = new ThrowSolution(Vector3.Normalize(direction), flightSeconds);
        return true;
    }

    public static Vector3 PositionAt(
        Vector3 origin,
        Vector3 direction,
        float speed,
        float gravity,
        float seconds)
        => origin
           + direction * (speed * seconds)
           - Vector3.UnitY * (0.5f * gravity * seconds * seconds);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z);
}
