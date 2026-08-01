using Stride.Core;
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
                LineRenderer.DrawDepthTestedLine(previous, point, shell);
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
            LineRenderer.DrawDepthTestedLine(
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
    public required PlayerRegistry Players { get; init; }

    private const string ExplosionSound = "assets/sfx/grenade_explosion.wav";

    /// <summary>Past WeaponFx.DistantReportMetres a blast is a rumble from elsewhere, and gets its
    /// own recording for the same reason distant rifle fire does.</summary>
    private const string DistantExplosionSound = "assets/sfx/grenade_far_off_300m.wav";

    /// <summary>Where a far-off blast is placed: along the true bearing, at a range it can be heard
    /// from. The recording already sounds distant — see PlayShotReport for the full reasoning.</summary>
    private const float DistantExplosionRange = 30f;

    /// <summary>
    /// Trauma from a grenade at your feet. Full, because there is nothing worse to save the top of
    /// the range for.
    /// </summary>
    private const float MaximumTrauma = 1f;

    /// <summary>
    /// How far out a blast is still felt. Wider than GrenadeConfig.DamageRadius on purpose — one
    /// that lands just outside its damage radius should still rattle you, and a shake that stops
    /// exactly where the damage stops tells the player precisely how safe they were.
    /// </summary>
    private const float ShakeRadius = GrenadeConfig.DamageRadius * 1.6f;

    private SoundManager sound = null!;

    public override void Start()
    {
        sound = Services.GetSafeServiceAs<SoundManager>();
        Objects.ObjectDespawned += OnObjectDespawned;
    }

    public override void Update() { }

    public override void Cancel()
    {
        Objects.ObjectDespawned -= OnObjectDespawned;
        ExplosionManager.Clear();
    }

    private void OnObjectDespawned(NetObject obj)
    {
        if (obj.Type != ObjectType.Grenade) return;

        var position = obj.Transform.Position;
        ExplosionManager.Spawn(position.ToStride());

        if (Players.LocalPlayer is not { IsDead: false } local)
        {
            sound.PlayOneShotSpatial(ExplosionSound, position.ToStride(), falloff: SoundFalloff.Explosion);
            return;
        }

        var ear = Digging.Eye(local.Position);
        var toBlast = position - ear;
        float range = toBlast.Length();
        if (range >= WeaponFx.DistantReportMetres)
        {
            var bearing = range > 1e-3f
                ? System.Numerics.Vector3.Normalize(toBlast)
                : System.Numerics.Vector3.UnitZ;
            sound.PlayOneShotSpatial(
                DistantExplosionSound,
                (ear + bearing * DistantExplosionRange).ToStride(),
                falloff: SoundFalloff.DistantReport);
        }
        else
        {
            sound.PlayOneShotSpatial(ExplosionSound, position.ToStride(), falloff: SoundFalloff.Explosion);
        }

        // LINEAR in distance, deliberately. CameraTrauma already squares trauma to get its shake,
        // so squaring the proximity here as well cubes the falloff: a blast twelve metres away came
        // out at two hundredths of a degree, which is nothing. Distance is to the eye rather than
        // the feet, because that is where the camera being shaken actually is.
        float closeness = 1f - MathUtil.Clamp(range / ShakeRadius, 0f, 1f);
        CameraTrauma.Add(MaximumTrauma * closeness);
    }
}
