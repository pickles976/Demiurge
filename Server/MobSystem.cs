using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Minimal server-side mob driver. Mobs are still ServerPlayers: this class only chooses intent,
    /// while movement, replication, health, equipped items, and weapon hit detection stay on the
    /// existing player/object systems.
    /// </summary>
    internal sealed class MobSystem
    {
        public const float RoamRadius = 50f;

        private const float ArriveDistance = 1.25f;
        private readonly ChunkMap terrain;
        private readonly Random random;
        private readonly Dictionary<ushort, Vector3> destinations = new();
        private readonly Dictionary<ushort, Vector3> homes = new();

        public MobSystem(ChunkMap terrain, int seed = 0x51A7)
        {
            this.terrain = terrain;
            random = new Random(seed);
        }

        public Vector3 RandomSpawnPoint() => RandomSurfacePoint(Vector3.Zero);

        public ServerPlayer CreateMob(ushort id, Vector3 position)
        {
            var mob = new ServerPlayer
            {
                Id = id,
                IsMob = true,
                Move = PlayerMovement.SpawnAt(terrain, position.X, position.Z),
            };
            homes[mob.Id] = mob.Position;
            destinations[mob.Id] = RandomSurfacePoint(mob.Position);
            return mob;
        }

        public void Step(ServerPlayer mob, float dt)
        {
            if (!destinations.TryGetValue(mob.Id, out var destination))
                destination = destinations[mob.Id] = RandomSurfacePoint(HomeOf(mob));

            var flat = destination - mob.Position;
            flat.Y = 0f;

            if (flat.LengthSquared() <= ArriveDistance * ArriveDistance)
            {
                destination = destinations[mob.Id] = RandomSurfacePoint(HomeOf(mob));
                flat = destination - mob.Position;
                flat.Y = 0f;
            }

            var intent = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.Zero;
            mob.State = PlayerStateFlags.None
                .With(PlayerStateFlags.Moving, intent != Vector3.Zero);
            mob.Yaw = intent != Vector3.Zero ? MathF.Atan2(intent.X, intent.Z) : mob.Yaw;
            mob.Pitch = 0f;
            mob.LastIntent = intent;

            PlayerMovement.Step(terrain, ref mob.Move, intent, mob.State, dt);
        }

        private Vector3 HomeOf(ServerPlayer mob)
            => homes.TryGetValue(mob.Id, out var home) ? home : mob.Position;

        private Vector3 RandomSurfacePoint(Vector3 center)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                float angle = (float)(random.NextDouble() * MathF.Tau);
                float distance = MathF.Sqrt((float)random.NextDouble()) * RoamRadius;
                float x = center.X + MathF.Cos(angle) * distance;
                float z = center.Z + MathF.Sin(angle) * distance;
                var chunk = ChunkTransforms.ChunkAt((int)MathF.Floor(x), (int)MathF.Floor(z));
                if (chunk.x < WorldGen.MeshableMin.x || chunk.x > WorldGen.MeshableMax.x
                    || chunk.z < WorldGen.MeshableMin.z || chunk.z > WorldGen.MeshableMax.z)
                    continue;
                return SurfaceQuery.SurfacePosition(terrain, x, z);
            }

            return SurfaceQuery.SurfacePosition(terrain, center.X, center.Z);
        }
    }
}
