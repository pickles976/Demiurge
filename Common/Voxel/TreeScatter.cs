using System.Numerics;

namespace Demiurge
{
    /// <summary>Where a tree could stand, and which way it would face.</summary>
    public readonly record struct TreeCandidate(Vector2 Position, float Yaw);

    /// <summary>
    /// Candidate tree positions for the grove brush: a jittered lattice — cellular noise, one point
    /// per cell, displaced by a hash of that cell's integer coordinate.
    ///
    /// The lattice is anchored to the WORLD, not to the brush stroke, and everything useful follows
    /// from that one decision rather than from code written to produce it:
    ///
    /// - Painting the same ground twice adds nothing, because the second stroke proposes the same
    ///   points and they are already taken.
    /// - Two overlapping strokes cannot clump along their seam.
    /// - Minimum spacing holds between trees from DIFFERENT strokes, not merely within one.
    /// - Erasing and repainting returns the same grove.
    ///
    /// The spacing guarantee is why the jitter is bounded. A point may leave its cell centre by at
    /// most <see cref="JitterFraction"/> of a cell on each axis, so two points in neighbouring cells
    /// can approach each other by at most twice that: the closest any pair can come is
    /// <c>cell * (1 - 2 * jitter)</c>, and it is the axis-adjacent pair that binds, since diagonal
    /// neighbours start a factor of root two further apart and close at the same rate. Callers state
    /// the spacing they want and <see cref="CellSizeFor"/> inverts it.
    ///
    /// Note what this does NOT guarantee: that a candidate is far from a tree somebody placed by
    /// hand, or from one painted at a different spacing. The lattice only knows about itself, so the
    /// brush still has to test its candidates against the trees that already exist.
    /// </summary>
    public static class TreeScatter
    {
        /// <summary>How far a point may sit from its cell centre, as a fraction of the cell.</summary>
        public const float JitterFraction = 0.25f;

        public const float MinimumSpacing = 1f;
        public const float MaximumSpacing = 40f;

        /// <summary>The lattice pitch that yields a requested minimum spacing.</summary>
        public static float CellSizeFor(float minSpacing)
            => Math.Clamp(minSpacing, MinimumSpacing, MaximumSpacing) / (1f - 2f * JitterFraction);

        /// <summary>
        /// Every candidate within <paramref name="radius"/> of a point. <paramref name="density"/>
        /// thins the lattice by a per-cell hash rather than by a draw, so the same circle at the
        /// same density always answers with the same trees — which is what lets a drag over the same
        /// ground be a repaint instead of a pile.
        /// </summary>
        public static List<TreeCandidate> InCircle(
            Vector2 centre, float radius, float minSpacing, float density)
        {
            float cell = CellSizeFor(minSpacing);
            float jitter = cell * JitterFraction;

            int minX = (int)MathF.Floor((centre.X - radius) / cell);
            int maxX = (int)MathF.Ceiling((centre.X + radius) / cell);
            int minZ = (int)MathF.Floor((centre.Y - radius) / cell);
            int maxZ = (int)MathF.Ceiling((centre.Y + radius) / cell);

            var candidates = new List<TreeCandidate>();

            for (int gx = minX; gx <= maxX; gx++)
            {
                for (int gz = minZ; gz <= maxZ; gz++)
                {
                    if (Hash01(gx, gz, 0) >= density) continue;

                    var position = new Vector2(
                        (gx + 0.5f) * cell + (Hash01(gx, gz, 1) * 2f - 1f) * jitter,
                        (gz + 0.5f) * cell + (Hash01(gx, gz, 2) * 2f - 1f) * jitter);

                    if (Vector2.DistanceSquared(position, centre) > radius * radius) continue;

                    candidates.Add(new TreeCandidate(position, Hash01(gx, gz, 3) * MathF.Tau));
                }
            }

            return candidates;
        }

        static float Hash01(int x, int z, int salt)
        {
            unchecked
            {
                uint h = (uint)x * 0x8DA6B343u
                       ^ (uint)z * 0xD8163841u
                       ^ (uint)salt * 0xCB1AB31Fu;
                h ^= h >> 13;
                h *= 0x85EBCA6Bu;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
