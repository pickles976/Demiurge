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

        /// <summary>
        /// How far the trunk base is buried. The model ends in a flat quad and the surface it stands
        /// on is smoothed and rarely level, so sitting it exactly on the sample leaves a visible gap
        /// under one side of the trunk.
        /// </summary>
        public const float SinkDepth = 0.5f;

        /// <summary>
        /// The trunk as a bullet blocker, measured from the placement position — which is
        /// <see cref="SinkDepth"/> below the surface, so the trunk starts underground and the part
        /// standing above it is shorter by that much.
        ///
        /// The model's trunk mesh is a one-metre square column 7.75 m tall, so this is the cylinder
        /// inscribed in it: a shot clips the corners of the drawn trunk rather than being stopped by
        /// air beside it, which is the side to err on.
        /// </summary>
        public const float TrunkRadius = 0.5f;
        public const float TrunkHeight = 7.75f;

        /// <summary>
        /// What it takes to kill a tree, in the same units a man's health is in — so a grenade that
        /// would kill a man outright does not quite fell a tree, and it takes real explosive or
        /// several to clear one.
        /// </summary>
        public const ushort MaxHealth = 50;

        /// <summary>
        /// The trees of one patch. The placement grid is fixed to the world rather than to the
        /// centre, so a patch holds exactly the trees a whole-map pass would have put there and
        /// growing the radius adds trees without moving the ones already standing.
        /// </summary>
        public static IReadOnlyList<TreeSpawn> Generate(ChunkMap map, Vector3 centre, float radius)
        {
            int gx0 = (int)MathF.Floor((centre.X - radius) / CellSize);
            int gx1 = (int)MathF.Floor((centre.X + radius) / CellSize);
            int gz0 = (int)MathF.Floor((centre.Z - radius) / CellSize);
            int gz1 = (int)MathF.Floor((centre.Z + radius) / CellSize);

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
                float dx = xs[i] - centre.X;
                float dz = zs[i] - centre.Z;
                if (dx * dx + dz * dz > radius * radius) continue;

                float n = Math.Clamp(density[i] / 0.70f, -1f, 1f);
                if (n < DensityThreshold) continue;
                if (Hash01(grid[i].X, grid[i].Z, 2) > n) continue;

                int worldX = (int)MathF.Floor(xs[i]);
                int worldZ = (int)MathF.Floor(zs[i]);
                if (!IsTreeEligible(map, worldX, worldZ, out var surface)) continue;

                float yaw = Hash01(grid[i].X, grid[i].Z, 3) * MathF.Tau;
                trees.Add(new TreeSpawn(new Vector3(xs[i], surface.Y - SinkDepth, zs[i]), yaw));
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
