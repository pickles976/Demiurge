using System.Numerics;

namespace Demiurge;

public interface INavGoal
{
    bool IsInGoal(NavCell cell);
    float Heuristic(NavCell cell);
}

public sealed record GoalPosition(NavCell Target) : INavGoal
{
    public bool IsInGoal(NavCell cell) => cell == Target;

    public float Heuristic(NavCell cell)
        => Distance(cell, Target) / NavCosts.MaxSpeed;

    public static float Distance(NavCell a, NavCell b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

public sealed record GoalNear(NavCell Target, float Radius) : INavGoal
{
    public bool IsInGoal(NavCell cell)
        => GoalPosition.Distance(cell, Target) <= Radius;

    public float Heuristic(NavCell cell)
        => MathF.Max(0f, GoalPosition.Distance(cell, Target) - Radius) / NavCosts.MaxSpeed;
}

public sealed record GoalAwayFrom(NavCell Threat, float Radius) : INavGoal
{
    public bool IsInGoal(NavCell cell)
        => GoalPosition.Distance(cell, Threat) >= Radius;

    public float Heuristic(NavCell cell)
        => MathF.Max(0f, Radius - GoalPosition.Distance(cell, Threat)) / NavCosts.MaxSpeed;
}
