using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Where the ground is. In Common because the server places things authoritatively and the client
    /// has to agree about where they landed.
    ///
    /// Not a heightmap lookup — the field is 3D, so "the surface" means where density crosses zero,
    /// and finding it means walking the column. That also means it keeps working once caves and
    /// player edits exist, where a column can have several surfaces.
    /// </summary>
    public static class SurfaceQuery
    {
        public readonly record struct SurfaceHit(float Y, BlockType Material);

        /// <summary>
        /// World Y of the highest surface in a column, or null if the column has no surface at all
        /// (entirely air, or a chunk that isn't loaded).
        ///
        /// Scans downward for the first air-over-solid pair and interpolates between them, so the
        /// result carries the same sub-voxel precision the mesher renders — spawn something here and
        /// it sits ON the ground rather than up to a voxel above or below it.
        /// </summary>
        public static float? HighestSurfaceY(ChunkMap map, int worldX, int worldZ)
            => HighestSurface(map, worldX, worldZ)?.Y;

        /// <summary>
        /// Highest surface in a column, plus the material of the solid voxel directly under it.
        /// That material is what the renderer shows on the surface triangle, and what foliage should
        /// use for spawn eligibility.
        /// </summary>
        public static SurfaceHit? HighestSurface(ChunkMap map, int worldX, int worldZ)
        {
            if (!map.TryGetVoxel(worldX, ChunkConstants.WorldMaxY - 1, worldZ, out var above)) return null;

            for (int y = ChunkConstants.WorldMaxY - 2; y >= ChunkConstants.WorldMinY; y--)
            {
                if (!map.TryGetVoxel(worldX, y, worldZ, out var below)) return null;

                // Solid below, air above: the surface is on this edge. Same convention as the mesher
                // — negative is solid, >= 0 is air.
                if (below.Distance < 0f && above.Distance >= 0f)
                {
                    float t = below.Distance / (below.Distance - above.Distance);
                    return new SurfaceHit(y + t, below.Material);
                }

                above = below;
            }

            return null;
        }

        /// <summary>
        /// Nearest floor crossing around a world-space point. Unlike <see cref="HighestSurface"/>,
        /// this keeps an editor placement on the local trench or cave floor instead of moving it to
        /// unrelated terrain higher in the same column.
        /// </summary>
        public static float? NearestSurfaceY(
            ChunkMap map,
            float worldX,
            float worldZ,
            float targetY,
            float maxDistance = 1.5f)
        {
            if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)
                || !float.IsFinite(targetY) || !float.IsFinite(maxDistance)
                || maxDistance < 0f)
                throw new ArgumentOutOfRangeException(nameof(maxDistance));

            int firstY = Math.Max(
                ChunkConstants.WorldMinY,
                (int)MathF.Floor(targetY - maxDistance) - 1);
            int lastY = Math.Min(
                ChunkConstants.WorldMaxY - 2,
                (int)MathF.Ceiling(targetY + maxDistance));

            float? nearest = null;
            float nearestDistance = float.MaxValue;
            for (int y = firstY; y <= lastY; y++)
            {
                if (!TerrainCollision.TrySampleRaw(
                        map, new Vector3(worldX, y, worldZ), out float below)
                    || !TerrainCollision.TrySampleRaw(
                        map, new Vector3(worldX, y + 1f, worldZ), out float above))
                    continue;

                if (below >= 0f || above < 0f) continue;

                float denominator = below - above;
                float crossingY = MathF.Abs(denominator) < 1e-6f
                    ? y
                    : y + below / denominator;
                float distance = MathF.Abs(crossingY - targetY);
                if (distance > maxDistance || distance >= nearestDistance) continue;

                nearest = crossingY;
                nearestDistance = distance;
            }

            return nearest;
        }

        /// <summary>
        /// A spawn position on the surface at the given world column: the same X/Z, with Y on the
        /// ground. Falls back to the world floor if the column has no surface, so a caller can't get
        /// a silent null and place something at the origin.
        /// </summary>
        public static Vector3 SurfacePosition(ChunkMap map, float worldX, float worldZ)
        {
            int voxelX = (int)MathF.Floor(worldX);
            int voxelZ = (int)MathF.Floor(worldZ);

            float y = HighestSurfaceY(map, voxelX, voxelZ)
                   ?? ChunkConstants.WorldMinY + ChunkConstants.BedrockThickness;

            return new Vector3(worldX, y, worldZ);
        }
    }
}
