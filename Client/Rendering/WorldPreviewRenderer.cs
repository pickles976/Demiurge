using Stride.Core.Mathematics;

namespace Demiurge;

public static class WorldPreviewRenderer
{
    public static void Cube(System.Numerics.Vector3 min, System.Numerics.Vector3 max, Color color)
    {
        var p = new Vector3[8];
        p[0] = new Vector3(min.X, min.Y, min.Z);
        p[1] = new Vector3(max.X, min.Y, min.Z);
        p[2] = new Vector3(max.X, min.Y, max.Z);
        p[3] = new Vector3(min.X, min.Y, max.Z);
        p[4] = new Vector3(min.X, max.Y, min.Z);
        p[5] = new Vector3(max.X, max.Y, min.Z);
        p[6] = new Vector3(max.X, max.Y, max.Z);
        p[7] = new Vector3(min.X, max.Y, max.Z);

        DrawLoop(p, 0, 1, 2, 3, color);
        DrawLoop(p, 4, 5, 6, 7, color);
        for (int i = 0; i < 4; i++) LineRenderer.DrawLine(p[i], p[i + 4], color);
    }

    public static void Cell(Demiurge.Editor.Int3 cell, Color color)
        => Cube(
            new System.Numerics.Vector3(cell.X, cell.Y, cell.Z),
            new System.Numerics.Vector3(cell.X + 1, cell.Y + 1, cell.Z + 1),
            color);

    public static void VoxelSample(System.Numerics.Vector3 centre, Color color)
        => Cube(
            centre - new System.Numerics.Vector3(0.5f),
            centre + new System.Numerics.Vector3(0.5f),
            color);

    public static void Sphere(System.Numerics.Vector3 centre, float radius, Color color, int segments = 32)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3? previous = null;
            Vector3 first = default;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * MathUtil.TwoPi / segments;
                float a = MathF.Cos(angle) * radius;
                float b = MathF.Sin(angle) * radius;
                var point = axis switch
                {
                    0 => new Vector3(centre.X, centre.Y + a, centre.Z + b),
                    1 => new Vector3(centre.X + a, centre.Y, centre.Z + b),
                    _ => new Vector3(centre.X + a, centre.Y + b, centre.Z),
                };
                if (i == 0) first = point;
                if (previous is { } from) LineRenderer.DrawLine(from, point, color);
                previous = point;
            }
            if (previous is { } last) LineRenderer.DrawLine(last, first, color);
        }
    }

    private static void DrawLoop(Vector3[] p, int a, int b, int c, int d, Color color)
    {
        LineRenderer.DrawLine(p[a], p[b], color);
        LineRenderer.DrawLine(p[b], p[c], color);
        LineRenderer.DrawLine(p[c], p[d], color);
        LineRenderer.DrawLine(p[d], p[a], color);
    }
}
