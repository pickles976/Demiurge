using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// Decides which level of detail each part of the world is drawn at.
    ///
    /// Keyed off the PLAYER, never the camera. The fly camera can be anywhere — parked over a distant
    /// ridge, looking back at the player — and if LOD followed it, flying out would silently coarsen the
    /// terrain you are inspecting and flying in would refine it, so you would never see what the player
    /// actually sees. Following the player means the fly camera can get close to a coarse box and see
    /// exactly the geometry the player would be looking at from far away, which is the point of having a
    /// free camera at all.
    ///
    /// The subdivision is a quadtree: start at the coarsest level and split a node when the player is
    /// near it. Splitting on the node's nearest point rather than its centre keeps the rings even.
    /// </summary>
    public static class TerrainLod
    {
        /// <summary>
        /// Split a node of this level when the player is within this many WORLD UNITS of it. Index is the
        /// node's level, so [1] is the LOD 1 -> LOD 0 boundary and [2] the LOD 2 -> LOD 1 one. Level 0
        /// never splits, hence the unused zero.
        /// </summary>
        static readonly float[] SplitWithin = { 0f, 112f, 224f };

        /// <summary>
        /// Every box that should currently exist. Cleared and refilled — this runs when the player
        /// crosses a chunk boundary, not every frame.
        /// </summary>
        public static void CollectDesired(Vector3 player, HashSet<LodSection> desired)
        {
            desired.Clear();

            int chunksPerRoot = 1 << LodSection.MaxLevel;

            for (int rx = FloorDiv(WorldGen.MeshableMin.x, chunksPerRoot); rx <= FloorDiv(WorldGen.MeshableMax.x, chunksPerRoot); rx++)
                for (int rz = FloorDiv(WorldGen.MeshableMin.z, chunksPerRoot); rz <= FloorDiv(WorldGen.MeshableMax.z, chunksPerRoot); rz++)
                    Walk(LodSection.MaxLevel, rx, rz, player, desired);
        }

        static void Walk(int level, int x, int z, Vector3 player, HashSet<LodSection> desired)
        {
            int chunks = 1 << level;

            int minChunkX = x * chunks, maxChunkX = minChunkX + chunks - 1;
            int minChunkZ = z * chunks, maxChunkZ = minChunkZ + chunks - 1;

            bool anyMeshable = maxChunkX >= WorldGen.MeshableMin.x && minChunkX <= WorldGen.MeshableMax.x
                            && maxChunkZ >= WorldGen.MeshableMin.z && minChunkZ <= WorldGen.MeshableMax.z;

            if (!anyMeshable) return;

            bool allMeshable = minChunkX >= WorldGen.MeshableMin.x && maxChunkX <= WorldGen.MeshableMax.x
                            && minChunkZ >= WorldGen.MeshableMin.z && maxChunkZ <= WorldGen.MeshableMax.z;

            // Split when the player is close, and also when the node straddles the world edge — a coarse
            // box there would read chunks that will never arrive and retry forever. That degrades the
            // outermost ring or two to fine detail, which is wasteful but correct.
            if (level > 0 && (!allMeshable || IsNear(level, minChunkX, minChunkZ, chunks, player)))
            {
                for (int dx = 0; dx < 2; dx++)
                    for (int dz = 0; dz < 2; dz++)
                        Walk(level - 1, x * 2 + dx, z * 2 + dz, player, desired);

                return;
            }

            if (!allMeshable) return;   // level 0, partly outside: nothing to draw

            for (int y = 0; y < LodSection.SectionsPerColumn(level); y++)
                desired.Add(new LodSection(x, y, z, level));
        }

        /// <summary>Distance from the player to the node's box on the ground plane. Height is ignored.</summary>
        static bool IsNear(int level, int minChunkX, int minChunkZ, int chunks, Vector3 player)
        {
            float minX = minChunkX * ChunkConstants.ChunkWidth;
            float minZ = minChunkZ * ChunkConstants.ChunkWidth;
            float size = chunks * ChunkConstants.ChunkWidth;

            float dx = MathF.Max(0f, MathF.Max(minX - player.X, player.X - (minX + size)));
            float dz = MathF.Max(0f, MathF.Max(minZ - player.Z, player.Z - (minZ + size)));

            return dx * dx + dz * dz < SplitWithin[level] * SplitWithin[level];
        }

        static int FloorDiv(int a, int b) => (a >= 0 ? a : a - b + 1) / b;
    }
}
