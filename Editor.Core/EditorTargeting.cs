using System.Numerics;

namespace Demiurge.Editor;

public readonly record struct EditorTargetCells(Int3 Solid, Int3 Air);

public static class EditorTargeting
{
    private const float SurfaceEpsilon = 0.01f;

    public static EditorTargetCells Cells(Vector3 hitPoint, Vector3 normal)
    {
        if (!IsFinite(hitPoint) || !IsFinite(normal) || normal.LengthSquared() < 1e-8f)
            throw new ArgumentException("Target hit and normal must be finite and non-zero");

        normal = Vector3.Normalize(normal);
        return new EditorTargetCells(
            CellAt(hitPoint - normal * SurfaceEpsilon),
            CellAt(hitPoint + normal * SurfaceEpsilon));
    }

    public static Int3 CellAt(Vector3 point)
        => new((int)MathF.Floor(point.X), (int)MathF.Floor(point.Y), (int)MathF.Floor(point.Z));

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
