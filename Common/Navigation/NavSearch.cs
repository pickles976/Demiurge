using System.Diagnostics;

namespace Demiurge;

public readonly record struct NavSearchOptions(
    TimeSpan PrimaryBudget,
    TimeSpan FailureBudget,
    int MaximumExpandedNodes,
    float MinimumPartialDistance)
{
    public static NavSearchOptions Default => new(
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(100),
        100_000,
        3f);
}

/// <summary>
/// Bounded deterministic A*. Wall-clock checks are amortized every 64 expansions and any bounded
/// search can return its best useful prefix instead of blocking a server tick for a perfect path.
/// </summary>
public static class NavSearch
{
    private sealed class Node
    {
        public required NavCell Cell { get; init; }
        public float Cost = NavCosts.Inf;
        public float Heuristic;
        public long Parent;
        public NavAction ActionFromParent;
        public bool HasParent;
        public bool Closed;
    }

    private static readonly (int X, int Z)[] Directions =
    [
        (0, 1),
        (1, 0),
        (0, -1),
        (-1, 0),
        (1, 1),
        (1, -1),
        (-1, -1),
        (-1, 1),
    ];

    public static NavPath Find(
        ChunkMap map,
        NavCell start,
        INavGoal goal,
        NavSearchOptions? requestedOptions = null)
    {
        var options = requestedOptions ?? NavSearchOptions.Default;
        if (options.MaximumExpandedNodes <= 0
            || options.PrimaryBudget < TimeSpan.Zero
            || options.FailureBudget < options.PrimaryBudget
            || !NavTraversal.Standable(map, start.X, start.Y, start.Z, out _))
            return NavPath.Failed();

        var nodes = new Dictionary<long, Node>();
        var open = new NavHeap();
        var startNode = new Node
        {
            Cell = start,
            Cost = 0f,
            Heuristic = goal.Heuristic(start),
        };
        nodes[start.Key] = startNode;
        open.EnqueueOrDecrease(start.Key, startNode.Heuristic);

        Node best = startNode;
        int expanded = 0;
        long started = Stopwatch.GetTimestamp();
        long primaryTicks = BudgetTicks(options.PrimaryBudget);
        long failureTicks = BudgetTicks(options.FailureBudget);

        while (open.TryDequeue(out long key))
        {
            var current = nodes[key];
            if (current.Closed) continue;
            current.Closed = true;
            expanded++;

            if (goal.IsInGoal(current.Cell))
                return Reconstruct(map, nodes, current, reachedGoal: true, expanded);

            if (current.Heuristic < best.Heuristic)
                best = current;

            if (expanded >= options.MaximumExpandedNodes)
                break;

            if ((expanded & 63) == 0)
            {
                long elapsed = Stopwatch.GetTimestamp() - started;
                bool usefulPartial =
                    GoalPosition.Distance(start, best.Cell) >= options.MinimumPartialDistance;
                if (elapsed >= failureTicks || elapsed >= primaryTicks && usefulPartial)
                    break;
            }

            foreach (var direction in Directions)
                VisitNeighbour(map, goal, current, direction.X, direction.Z, nodes, open);
        }

        return best.Cell == start
            ? NavPath.Failed(expanded)
            : Reconstruct(map, nodes, best, reachedGoal: false, expanded);
    }

    private static void VisitNeighbour(
        ChunkMap map,
        INavGoal goal,
        Node current,
        int dx,
        int dz,
        Dictionary<long, Node> nodes,
        NavHeap open)
    {
        int x = current.Cell.X + dx;
        int z = current.Cell.Z + dz;
        float edgeCost = NavCosts.Inf;
        bool traversable = NavTraversal.TryFindStandable(
                map,
                x,
                z,
                current.Cell.Y,
                NavTraversal.MaximumTraverseCellDelta,
                NavTraversal.MaximumTraverseCellDelta,
                out var next,
                out _)
            && NavTraversal.TryStep(map, current.Cell, next, out edgeCost);

        if (traversable)
        {
            if (dx != 0 && dz != 0
                && (!CardinalClear(map, current.Cell, dx, 0)
                    || !CardinalClear(map, current.Cell, 0, dz)))
                return;
            Relax(goal, current, next, edgeCost, NavAction.Walk, nodes, open);
            return;
        }

        if (dx != 0 && dz != 0
            || !NavTraversal.TryJump(
                map,
                current.Cell,
                dx,
                dz,
                out next,
                out edgeCost))
            return;

        Relax(goal, current, next, edgeCost, NavAction.Jump, nodes, open);
    }

    private static void Relax(
        INavGoal goal,
        Node current,
        NavCell next,
        float edgeCost,
        NavAction action,
        Dictionary<long, Node> nodes,
        NavHeap open)
    {
        float candidateCost = current.Cost + edgeCost;
        if (candidateCost >= NavCosts.Inf) return;
        if (!nodes.TryGetValue(next.Key, out var node))
        {
            node = new Node
            {
                Cell = next,
                Heuristic = goal.Heuristic(next),
            };
            nodes[next.Key] = node;
        }
        if (node.Closed || candidateCost >= node.Cost) return;

        node.Cost = candidateCost;
        node.Parent = current.Cell.Key;
        node.ActionFromParent = action;
        node.HasParent = true;
        open.EnqueueOrDecrease(next.Key, node.Cost + node.Heuristic);
    }

    private static bool CardinalClear(ChunkMap map, NavCell current, int dx, int dz)
        => NavTraversal.TryFindStandable(
               map,
               current.X + dx,
               current.Z + dz,
               current.Y,
               NavTraversal.MaximumTraverseCellDelta,
               NavTraversal.MaximumTraverseCellDelta,
               out var adjacent,
               out _)
           && NavTraversal.TryStep(map, current, adjacent, out _);

    private static NavPath Reconstruct(
        ChunkMap map,
        Dictionary<long, Node> nodes,
        Node end,
        bool reachedGoal,
        int expanded)
    {
        var reversed = new List<NavWaypoint>();
        var node = end;
        while (true)
        {
            reversed.Add(new NavWaypoint(
                node.Cell,
                NavTraversal.Position(map, node.Cell),
                node.ActionFromParent));
            if (!node.HasParent) break;
            node = nodes[node.Parent];
        }
        reversed.Reverse();
        return new NavPath(reversed, reachedGoal, end.Cost, expanded);
    }

    private static long BudgetTicks(TimeSpan budget)
        => (long)Math.Ceiling(budget.TotalSeconds * Stopwatch.Frequency);
}
