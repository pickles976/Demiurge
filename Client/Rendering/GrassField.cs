using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// Terrain-following grass source. It streams one seed per grassable voxel column into
    /// <see cref="GpuGrassRenderer"/>, which expands them into chunk-local blade meshes.
    /// </summary>
    public static class GrassField
    {
        public const float DefaultRadius = 45f;
        public const float DefaultCellSize = 1f;

        public static Entity CreateFollower(PlayerRegistry registry, TerrainState terrain)
            => new("GrassField")
            {
                new GrassFollowerScript
                {
                    Registry = registry,
                    Terrain = terrain,
                    Radius = DefaultRadius,
                    CellSize = DefaultCellSize,
                },
            };
    }

    public sealed class GrassFollowerScript : SyncScript
    {
        private const int MaxSeedChunksPerFrame = 1;

        public required PlayerRegistry Registry { get; init; }
        public required TerrainState Terrain { get; init; }

        public float Radius { get; init; } = GrassField.DefaultRadius;
        public float CellSize { get; init; } = GrassField.DefaultCellSize;
        public int MaxInstances { get; init; } = 500_000;

        private readonly Dictionary<ChunkIndex, ChunkIndex> active = new();
        private readonly HashSet<ChunkIndex> dirty = new();
        private GpuGrassRenderer? renderer;
        private ChunkIndex? lastAnchor;

        public override void Start()
        {
            renderer = new GpuGrassRenderer(Services, ((Game)Game).GraphicsDevice, MaxInstances);
            renderer.SetGrassDistance(Radius);
            renderer.SetLodParams(multiplier: 0.85f, exponent: 1.7f);
            renderer.SetWind(strength: 0.12f, speed: 1.4f, frequency: 0.75f);
            renderer.GrassEntity.Scene = Entity.Scene;

            // Named handlers rather than lambdas so Cancel can take them off again. It matters: in a
            // playtest the TerrainState belongs to the EDITOR session and outlives this one, so a
            // subscription left behind would keep calling into a dead script's collections every
            // time the editor streamed or edited a chunk.
            Terrain.ChunkCompleted += OnChunkCompleted;
            Terrain.RegionEdited += MarkEditedChunks;
        }

        private void OnChunkCompleted(ChunkIndex chunk) => dirty.Add(chunk);

        public override void Update()
        {
            if (renderer == null) return;
            if (Registry.LocalPlayer is not { } local)
            {
                renderer.ClearSeeds();
                return;
            }

            var playerPosition = local.Position.ToStride();
            RefreshActiveChunks(playerPosition);
            RebuildDirtyChunks();

            renderer.ClearTrampleSources();
            renderer.AddTrampleSource(playerPosition, radius: 1.4f);
            renderer.Update(playerPosition, (Game)Game);
        }

        public override void Cancel()
        {
            Terrain.ChunkCompleted -= OnChunkCompleted;
            Terrain.RegionEdited -= MarkEditedChunks;
            renderer?.Dispose();
            renderer = null;
            active.Clear();
            dirty.Clear();
        }

        private void RefreshActiveChunks(Vector3 center)
        {
            var anchor = ChunkTransforms.ChunkAt(center);
            if (lastAnchor is { } previous && previous.Equals(anchor) && dirty.Count == 0) return;

            lastAnchor = anchor;
            float radiusSq = Radius * Radius;
            var min = ChunkTransforms.ChunkAt(
                (int)MathF.Floor(center.X - Radius),
                (int)MathF.Floor(center.Z - Radius));
            var max = ChunkTransforms.ChunkAt(
                (int)MathF.Floor(center.X + Radius),
                (int)MathF.Floor(center.Z + Radius));
            var wanted = new HashSet<ChunkIndex>();

            for (int z = min.z; z <= max.z; z++)
            {
                for (int x = min.x; x <= max.x; x++)
                {
                    var chunk = new ChunkIndex { x = x, z = z };
                    if (!Terrain.IsComplete(chunk)) continue;

                    var (originX, originZ) = ChunkTransforms.ChunkOrigin(chunk);
                    float nearestX = MathUtil.Clamp(center.X, originX, originX + ChunkConstants.ChunkWidth);
                    float nearestZ = MathUtil.Clamp(center.Z, originZ, originZ + ChunkConstants.ChunkWidth);
                    float dx = nearestX - center.X;
                    float dz = nearestZ - center.Z;
                    if (dx * dx + dz * dz > radiusSq) continue;

                    wanted.Add(chunk);
                    if (!active.ContainsKey(chunk))
                    {
                        active[chunk] = chunk;
                        dirty.Add(chunk);
                    }
                }
            }

            foreach (var chunk in active.Keys.ToArray())
            {
                if (wanted.Contains(chunk)) continue;

                active.Remove(chunk);
                dirty.Remove(chunk);
                renderer?.RemoveChunk(ChunkKey(chunk));
            }
        }

        private void RebuildDirtyChunks()
        {
            if (renderer == null) return;

            int rebuilt = 0;
            foreach (var chunk in dirty.ToArray())
            {
                dirty.Remove(chunk);
                if (!active.ContainsKey(chunk) || !Terrain.IsComplete(chunk)) continue;

                var seeds = BuildChunkSeeds(chunk);
                renderer.SetChunkSeeds(ChunkKey(chunk), seeds);
                if (++rebuilt >= MaxSeedChunksPerFrame) break;
            }
        }

        private GrassSeed[] BuildChunkSeeds(ChunkIndex chunk)
        {
            var (originX, originZ) = ChunkTransforms.ChunkOrigin(chunk);
            int step = Math.Max(1, (int)MathF.Round(CellSize));
            var seeds = new List<GrassSeed>((ChunkConstants.ChunkWidth / step) * (ChunkConstants.ChunkWidth / step));

            for (int lx = 0; lx < ChunkConstants.ChunkWidth; lx += step)
            {
                for (int lz = 0; lz < ChunkConstants.ChunkWidth; lz += step)
                {
                    int worldX = originX + lx;
                    int worldZ = originZ + lz;

                    if (SurfaceQuery.HighestSurface(Terrain.Map, worldX, worldZ) is not { } surface) continue;
                    if (surface.Material != BlockType.BlockType_Grass) continue;

                    var position = new Vector3(worldX, surface.Y + 0.02f, worldZ);
                    seeds.Add(new GrassSeed(position, GrassScatter.HashCell(worldX, worldZ, seed: 17)));
                }
            }

            return seeds.ToArray();
        }

        private void MarkEditedChunks(System.Numerics.Vector3 min, System.Numerics.Vector3 max)
        {
            var first = ChunkTransforms.ChunkAt((int)MathF.Floor(min.X), (int)MathF.Floor(min.Z));
            var last = ChunkTransforms.ChunkAt((int)MathF.Floor(max.X), (int)MathF.Floor(max.Z));

            for (int z = first.z; z <= last.z; z++)
                for (int x = first.x; x <= last.x; x++)
                    dirty.Add(new ChunkIndex { x = x, z = z });
        }

        private static Int3 ChunkKey(ChunkIndex chunk) => new(chunk.x, 0, chunk.z);
    }
}
