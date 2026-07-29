using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge;

/// <summary>
/// Temporary line-rendered grenade burst. It is intentionally isolated behind Spawn/Update so a
/// particle implementation can replace it without touching grenade gameplay or replication.
/// </summary>
public static class ExplosionManager
{
    private const float Lifetime = 0.55f;
    private const float MaxRadius = 2.1f;
    private const int RingSegments = 20;
    private const int RayCount = 18;

    private struct Explosion
    {
        public Vector3 Centre;
        public float Age;
        public int Seed;
    }

    private static readonly List<Explosion> explosions = [];
    private static int nextSeed;

    public static void Spawn(Vector3 centre)
        => explosions.Add(new Explosion { Centre = centre, Seed = nextSeed++ });

    public static void Clear() => explosions.Clear();

    public static void Update(float dt)
    {
        for (int i = explosions.Count - 1; i >= 0; i--)
        {
            var explosion = explosions[i];
            explosion.Age += dt;
            if (explosion.Age >= Lifetime)
            {
                explosions.RemoveAt(i);
                continue;
            }

            Draw(explosion);
            explosions[i] = explosion;
        }
    }

    private static void Draw(in Explosion explosion)
    {
        float t = explosion.Age / Lifetime;
        float radius = MaxRadius * (1f - MathF.Pow(1f - t, 3f));
        byte alpha = (byte)(MathUtil.Clamp(1f - t, 0f, 1f) * 235f);
        var shell = new Color(255, 150, 35, alpha);
        var core = new Color(255, 230, 125, (byte)(alpha * 0.85f));

        // Three great circles make the expanding shell readable from every camera angle.
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 previous = PointOnRing(explosion.Centre, radius, axis, RingSegments - 1);
            for (int segment = 0; segment < RingSegments; segment++)
            {
                var point = PointOnRing(explosion.Centre, radius, axis, segment);
                LineRenderer.DrawLine(previous, point, shell);
                previous = point;
            }
        }

        // Fibonacci-sphere rays avoid a planar firework look. The seed rotates the distribution so
        // repeated explosions do not share an obvious silhouette.
        const float goldenAngle = 2.39996323f;
        float rotation = Hash(explosion.Seed) * MathUtil.TwoPi;
        for (int ray = 0; ray < RayCount; ray++)
        {
            float y = 1f - 2f * ((ray + 0.5f) / RayCount);
            float radial = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            float angle = ray * goldenAngle + rotation;
            var direction = new Vector3(
                MathF.Cos(angle) * radial,
                y,
                MathF.Sin(angle) * radial);
            float length = radius * (0.75f + 0.35f * Hash(explosion.Seed + ray + 1));
            LineRenderer.DrawLine(
                explosion.Centre + direction * (radius * 0.12f),
                explosion.Centre + direction * length,
                core);
        }
    }

    private static Vector3 PointOnRing(
        Vector3 centre,
        float radius,
        int axis,
        int segment)
    {
        float angle = segment * MathUtil.TwoPi / RingSegments;
        float a = MathF.Cos(angle) * radius;
        float b = MathF.Sin(angle) * radius;
        return axis switch
        {
            0 => centre + new Vector3(0f, a, b),
            1 => centre + new Vector3(a, 0f, b),
            _ => centre + new Vector3(a, b, 0f),
        };
    }

    private static float Hash(int value)
    {
        uint hash = (uint)value * 0x9E3779B9u + 0x85EBCA6Bu;
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        return (hash & 0x00FFFFFFu) / (float)0x01000000;
    }
}

/// <summary>Turns the replicated grenade despawn into a client-only line burst.</summary>
public sealed class GrenadeExplosionScript : SyncScript
{
    public required ObjectRegistry Objects { get; init; }

    public override void Start() => Objects.ObjectDespawned += OnObjectDespawned;
    public override void Update() { }

    public override void Cancel()
    {
        Objects.ObjectDespawned -= OnObjectDespawned;
        ExplosionManager.Clear();
    }

    private static void OnObjectDespawned(NetObject obj)
    {
        if (obj.Type == ObjectType.Grenade)
            ExplosionManager.Spawn(obj.Transform.Position.ToStride());
    }
}
