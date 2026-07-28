using NoiseDotNet;
using System.Numerics;

namespace Demiurge
{
    public readonly record struct TreeSpawn(Vector3 Position, float Yaw);

    /// <summary>
    /// Deterministic tree placement over the authoritative terrain. Trees are objects, not terrain,
    /// but the eligibility test reads the same surface material the grass renderer uses.
    /// </summary>
    public static class TreePlacement
    {
        public const float CellSize = 5.37f; // 12 / sqrt(5): five times as many placement cells.
        public const float DensityWavelength = 90f;
        public const float DensityThreshold = 0.34f;
        private const int DensitySeed = NoiseGen.Seed + 500;

        public static IReadOnlyList<TreeSpawn> Generate(ChunkMap map)
        {
            int minX = WorldGen.MeshableMin.x * ChunkConstants.ChunkWidth;
            int maxX = (WorldGen.MeshableMax.x + 1) * ChunkConstants.ChunkWidth - 1;
            int minZ = WorldGen.MeshableMin.z * ChunkConstants.ChunkWidth;
            int maxZ = (WorldGen.MeshableMax.z + 1) * ChunkConstants.ChunkWidth - 1;

            int gx0 = (int)MathF.Floor(minX / CellSize);
            int gx1 = (int)MathF.Floor(maxX / CellSize);
            int gz0 = (int)MathF.Floor(minZ / CellSize);
            int gz1 = (int)MathF.Floor(maxZ / CellSize);

            int count = (gx1 - gx0 + 1) * (gz1 - gz0 + 1);
            var xs = new float[count];
            var zs = new float[count];
            var grid = new (int X, int Z)[count];

            int i = 0;
            for (int gx = gx0; gx <= gx1; gx++)
            {
                for (int gz = gz0; gz <= gz1; gz++)
                {
                    grid[i] = (gx, gz);
                    xs[i] = (gx + Hash01(gx, gz, 0)) * CellSize;
                    zs[i] = (gz + Hash01(gx, gz, 1)) * CellSize;
                    i++;
                }
            }

            var density = new float[count];
            Noise.GradientNoise2D(xs, zs, density, new NoiseSettings
            {
                XFrequency = 1f / DensityWavelength,
                YFrequency = 1f / DensityWavelength,
                Amplitude = 1f,
                Seed = DensitySeed,
            });

            var trees = new List<TreeSpawn>();
            for (i = 0; i < count; i++)
            {
                float n = Math.Clamp(density[i] / 0.70f, -1f, 1f);
                if (n < DensityThreshold) continue;
                if (Hash01(grid[i].X, grid[i].Z, 2) > n) continue;

                int worldX = (int)MathF.Floor(xs[i]);
                int worldZ = (int)MathF.Floor(zs[i]);
                if (!IsTreeEligible(map, worldX, worldZ, out var surface)) continue;

                float yaw = Hash01(grid[i].X, grid[i].Z, 3) * MathF.Tau;
                trees.Add(new TreeSpawn(new Vector3(xs[i], surface.Y, zs[i]), yaw));
            }

            return trees;
        }

        public static bool IsTreeEligible(ChunkMap map, int worldX, int worldZ, out SurfaceQuery.SurfaceHit surface)
        {
            surface = default;
            if (SurfaceQuery.HighestSurface(map, worldX, worldZ) is not { } hit) return false;
            if (hit.Material != BlockType.BlockType_Grass) return false;
            surface = hit;
            return true;
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
