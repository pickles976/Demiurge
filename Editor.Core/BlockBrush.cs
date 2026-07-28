namespace Demiurge.Editor;

/// <summary>Expands a rectangular block brush into ordinary sample-centred block placements.</summary>
public static class BlockBrush
{
    public static IEnumerable<Int3> Cells(Int3 anchor, Int3 size)
    {
        ValidateSize(size);

        int startX = -(size.X - 1) / 2;
        int startY = -(size.Y - 1) / 2;
        int startZ = -(size.Z - 1) / 2;

        for (int y = 0; y < size.Y; y++)
            for (int z = 0; z < size.Z; z++)
                for (int x = 0; x < size.X; x++)
                {
                    yield return new Int3(
                        anchor.X + startX + x,
                        anchor.Y + startY + y,
                        anchor.Z + startZ + z);
                }
    }

    public static (Int3 Min, Int3 Max) Bounds(Int3 anchor, Int3 size)
    {
        ValidateSize(size);
        int startX = -(size.X - 1) / 2;
        int startY = -(size.Y - 1) / 2;
        int startZ = -(size.Z - 1) / 2;
        int endX = startX + size.X - 1;
        int endY = startY + size.Y - 1;
        int endZ = startZ + size.Z - 1;

        return (
            new Int3(anchor.X + startX, anchor.Y + startY, anchor.Z + startZ),
            new Int3(anchor.X + endX, anchor.Y + endY, anchor.Z + endZ));
    }

    public static bool IsValidSize(Int3 size)
        => size.X is >= 1 and <= EditorToolSettings.MaxBlockBrushDimension
            && size.Y is >= 1 and <= EditorToolSettings.MaxBlockBrushDimension
            && size.Z is >= 1 and <= EditorToolSettings.MaxBlockBrushDimension
            && (long)size.X * size.Y * size.Z <= EditorToolSettings.MaxBlockBrushCells;

    public static void ValidateSize(Int3 size)
    {
        if (!IsValidSize(size))
            throw new ArgumentOutOfRangeException(
                nameof(size),
                $"Block dimensions must be from 1 to {EditorToolSettings.MaxBlockBrushDimension} " +
                $"and contain at most {EditorToolSettings.MaxBlockBrushCells} cells");
    }
}
