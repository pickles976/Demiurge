using System.Numerics;

namespace Demiurge;

/// <summary>
/// One walkable surface in a voxel column. Y identifies the solid SDF sample immediately below
/// the surface; X/Z identify the one-metre navigation column whose centre is sampled.
/// </summary>
public readonly record struct NavCell(int X, int Y, int Z)
{
    private const int HorizontalBits = 26;
    private const int HorizontalMask = (1 << HorizontalBits) - 1;
    private const int VerticalMask = (1 << 12) - 1;

    public long Key
        => ((long)(X & HorizontalMask) << 38)
         | ((long)(Z & HorizontalMask) << 12)
         | (long)(Y & VerticalMask);

    public Vector2 CentreXZ => new(X + 0.5f, Z + 0.5f);

    public static NavCell FromKey(long key)
        => new(
            SignExtend((int)((key >> 38) & HorizontalMask), HorizontalBits),
            (int)(key & VerticalMask),
            SignExtend((int)((key >> 12) & HorizontalMask), HorizontalBits));

    private static int SignExtend(int value, int bits)
    {
        int shift = 32 - bits;
        return value << shift >> shift;
    }
}
