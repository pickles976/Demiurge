using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge;

/// <summary>
/// Temporary line-rendered blast. It is intentionally isolated behind Spawn/Update so a particle
/// implementation can replace it without touching gameplay or replication.
///
/// Sized by the blast rather than fixed: a mortar bomb's burst is half again a grenade's, and an
/// effect that drew both the same would tell the player the wrong thing about the radius he is
/// standing in.
/// </summary>
public static class ExplosionManager
{
    private const float Lifetime = 0.55f;

    /// <summary>Shell radius for a GRENADE-sized blast; anything else scales off its damage radius.</summary>
    private const float MaxRadius = 2.1f;
    private const int RingSegments = 20;
    private const int RayCount = 18;

    private struct Explosion
    {
        public Vector3 Centre;
        public float Radius;
        public float Age;
        public int Seed;
    }

    private static readonly List<Explosion> explosions = [];
    private static int nextSeed;

    /// <summary>A blast of the given profile. The visual scales with the profile's damage radius,
    /// so the effect and the thing that hurt you stay the same size as each other.</summary>
    public static void Spawn(Vector3 centre, in BlastProfile blast)
        => explosions.Add(new Explosion
        {
            Centre = centre,
            Radius = MaxRadius * (blast.DamageRadius / GrenadeConfig.DamageRadius),
            Seed = nextSeed++,
        });

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
        float radius = explosion.Radius * (1f - MathF.Pow(1f - t, 3f));
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

/// <summary>
/// Turns the replicated despawn of anything that goes off into a client-only burst, shake and report.
///
/// Despawn is the signal because it is one the server already sends and the client already believes:
/// a grenade and a mortar bomb both stop existing at the moment they detonate, so nothing had to be
/// added to the wire to know where and when. The blast PROFILE is looked up from the object type
/// rather than replicated, for the same reason weapon damage is not on the wire.
/// </summary>
public sealed class BlastEffectScript : SyncScript
{
    public required ObjectRegistry Objects { get; init; }
    public required PlayerRegistry Players { get; init; }

    /// <summary>
    /// What one thing going off sounds like, near and far.
    ///
    /// Past WeaponFx.DistantReportMetres a blast is a rumble from elsewhere and gets its own
    /// recording, for the same reason distant rifle fire does. Both recordings live beside the blast
    /// profile rather than as globals because a grenade and a mortar bomb are not the same bang, and
    /// a mortar that borrowed the grenade's sample told the player the wrong thing about what had
    /// just landed near him.
    /// </summary>
    private readonly record struct Detonation(
        BlastProfile Blast,
        string Sound,
        string DistantSound);

    /// <summary>Where a far-off blast is placed: along the true bearing, at a range it can be heard
    /// from. The recording already sounds distant — see PlayShotReport for the full reasoning.</summary>
    private const float DistantExplosionRange = 30f;

    /// <summary>
    /// Trauma from a grenade at your feet. Over 1 on purpose: CameraTrauma clamps the pool, so the
    /// excess is not wasted — it means everything inside half the shake radius saturates rather than
    /// only a blast directly underfoot, and the falloff only starts biting further out.
    /// </summary>
    private const float MaximumTrauma = 2f;

    /// <summary>
    /// How far out a blast is still felt, as a multiple of its own damage radius. Much wider than the
    /// damage on purpose — one that lands well outside its damage radius should still rattle you, and
    /// a shake that stops where the damage stops tells the player precisely how safe they were.
    /// </summary>
    private const float ShakeRadiusScale = 3.2f;

    /// <summary>
    /// What each thing that despawns is worth as a bang, or null for everything that simply stopped
    /// existing. One table rather than a branch per weapon: a new explosive is a row here.
    /// </summary>
    private static Detonation? DetonationFor(ObjectType type) => type switch
    {
        ObjectType.Grenade => new Detonation(
            GrenadeConfig.Blast,
            "assets/sfx/grenade_explosion.wav",
            "assets/sfx/grenade_explosion_far.wav"),
        ObjectType.MortarRound => new Detonation(
            MortarConfig.Blast,
            "assets/sfx/mortar_explosion.wav",
            "assets/sfx/mortar_impact_far_off.wav"),
        _ => null,
    };

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
        if (DetonationFor(obj.Type) is not { } detonation) return;

        var blast = detonation.Blast;
        var position = obj.Transform.Position;
        ExplosionManager.Spawn(position.ToStride(), blast);

        if (Players.LocalPlayer is not { IsDead: false } local)
        {
            sound.PlayOneShotSpatial(
                detonation.Sound, position.ToStride(), falloff: SoundFalloff.Explosion);
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
                detonation.DistantSound,
                (ear + bearing * DistantExplosionRange).ToStride(),
                falloff: SoundFalloff.DistantReport);
        }
        else
        {
            sound.PlayOneShotSpatial(
                detonation.Sound, position.ToStride(), falloff: SoundFalloff.Explosion);
        }

        // LINEAR in distance, deliberately. CameraTrauma already squares trauma to get its shake,
        // so squaring the proximity here as well cubes the falloff: a blast twelve metres away came
        // out at two hundredths of a degree, which is nothing. Distance is to the eye rather than
        // the feet, because that is where the camera being shaken actually is.
        float closeness = 1f - MathUtil.Clamp(range / (blast.DamageRadius * ShakeRadiusScale), 0f, 1f);
        CameraTrauma.Add(MaximumTrauma * closeness);
    }
}
