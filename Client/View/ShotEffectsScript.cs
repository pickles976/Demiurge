using Demiurge;
using Demiurge.GameClient;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

// Shot feedback: traveling projectile tracer + positional audio, per weapon.
// Two inputs, one effects path:
//  - local player: the sim's ShotFired event, instantly on the PREDICTED shot;
//  - remote players: the server's projectile-spawn broadcast (accepted shots only),
//    filtered to skip our own id so predicted shots never double-flash.
// View-only — the server's projectile decides real hits. This simulation uses
// replicated positions and exists only to put the tracer and impact in the right place.
public class ShotEffectsScript : SyncScript
{
    public required PlayerRegistry Registry { get; init; }
    public required ObjectRegistry Objects { get; init; }
    public required NetworkManager Network { get; init; }
    public required TerrainState Terrain { get; init; }

    private sealed class VisualProjectile
    {
        public required System.Numerics.Vector3 Position { get; set; }
        public required System.Numerics.Vector3 Velocity { get; set; }
        public required float RemainingDistance { get; set; }
        public required ushort ShooterId { get; init; }
        public required Color Color { get; init; }
    }

    private readonly record struct VisualHit(
        System.Numerics.Vector3 Point,
        System.Numerics.Vector3? Normal,
        bool Flesh);

    private const float TracerLifetime = 0.055f;
    private const float MinTracerLength = 0.03f;

    /// <summary>Dust, not sparks: pale and a little transparent, so a burst of them reads as one
    /// kicked-up cloud rather than seven separate lines.</summary>
    private static readonly Color ImpactColor = new(214, 198, 172, 205);
    private static readonly Color FleshImpactColor = new(222, 44, 44, 230);
    private static readonly Color DamageTextColor = new(255, 64, 64, 255);

    private SoundManager sound = null!;
    private LocalPlayer? subscribed;
    private readonly List<VisualProjectile> projectiles = new();

    public override void Start()
    {
        sound = Services.GetSafeServiceAs<SoundManager>();
        Network.PlayerFired += OnRemoteFired;
        Network.HitConfirmed += OnHitConfirmed;
    }

    public override void Update()
    {
        // LocalPlayer appears on spawn (and can be replaced on reconnect):
        // keep the subscription pointed at the current instance.
        if (!ReferenceEquals(subscribed, Registry.LocalPlayer))
        {
            if (subscribed != null) subscribed.ShotFired -= OnLocalShot;
            subscribed = Registry.LocalPlayer;
            if (subscribed != null) subscribed.ShotFired += OnLocalShot;
        }

        UpdateProjectiles((float)Game.UpdateTime.Elapsed.TotalSeconds);
    }

    private void OnLocalShot(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction)
    {
        if (subscribed is not { IsArmed: true } local) return;   // ShotFired implies armed, but be safe
        if (local.Weapon!.Item.Type == ItemType.Grenade) return; // replicated sphere is the visual
        PlayEffects(origin, direction, local.Weapon!.Item.Type, Network.ClientId);
    }

    private void OnRemoteFired(PlayerFiredData data)
    {
        if (data.PlayerId == Network.ClientId) return;   // our shots already played predictively
        if (data.Weapon == ItemType.Grenade) return;
        // Unknown off-the-wire weapon types cannot provide a meaningful projectile speed.
        if (WeaponConfig.Get(data.Weapon) is null) return;
        PlayEffects(data.Origin, data.Direction, data.Weapon, data.PlayerId);
    }

    private void OnHitConfirmed(HitConfirmData confirm)
    {
        if (!Objects.TryGet(confirm.TargetNetworkId, out var target)) return;
        if (!target.Has.HasFlag(NetComponents.Owner)) return;
        if (!Registry.TryGet(target.Owner.PlayerId, out var player)) return;

        var position = player.Position + new System.Numerics.Vector3(0f, GunConfig.PlayerCenterHeight + 0.6f, 0f);
        DamageTextManager.Spawn(position.ToStride(), confirm.Damage, DamageTextColor);
    }

    private void PlayEffects(
        System.Numerics.Vector3 origin,
        System.Numerics.Vector3 direction,
        ItemType weapon,
        ushort shooterId)
    {
        var ballistics = BallisticsConfig.Require(weapon);
        if (!IsFinite(origin)
            || !IsFinite(direction)
            || direction.LengthSquared() < 1e-8f
            || ballistics.ProjectileSpeed <= 0f)
            return;
        direction = System.Numerics.Vector3.Normalize(direction);

        var fx = WeaponFx.Get(weapon);
        var start = origin.ToStride();
        sound.PlayOneShotSpatial(fx.ShotSoundPath, start);
        projectiles.Add(new VisualProjectile
        {
            Position = origin,
            Velocity = direction * ballistics.ProjectileSpeed,
            RemainingDistance = ProjectileMotion.SafetyDistance,
            ShooterId = shooterId,
            Color = fx.TracerColor,
        });
    }

    private void UpdateProjectiles(float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0f) return;

        for (int i = projectiles.Count - 1; i >= 0; i--)
        {
            var projectile = projectiles[i];
            var step = ProjectileMotion.Advance(
                projectile.Position,
                projectile.Velocity,
                dt,
                projectile.RemainingDistance);

            if (TryHit(step.Start, step.End, projectile.ShooterId, out var hit))
            {
                DrawSegment(step.Start, hit.Point, projectile.Color);
                if (hit.Flesh)
                    ImpactManager.Spawn(hit.Point.ToStride(), hit.Normal!.Value.ToStride(), FleshImpactColor);
                else if (hit.Normal is { } normal)
                    ImpactManager.Spawn(hit.Point.ToStride(), normal.ToStride(), ImpactColor);
                projectiles.RemoveAt(i);
                continue;
            }

            DrawSegment(step.Start, step.End, projectile.Color);
            projectile.Position = step.End;
            projectile.Velocity = step.Velocity;
            projectile.RemainingDistance -= step.Distance;
            if (step.Exhausted)
                projectiles.RemoveAt(i);
        }
    }

    private bool TryHit(
        System.Numerics.Vector3 start,
        System.Numerics.Vector3 end,
        ushort shooterId,
        out VisualHit hit)
    {
        hit = default;
        var segment = end - start;
        float length = segment.Length();
        if (length < 1e-6f) return false;
        var direction = segment / length;

        float nearest = float.MaxValue;
        if (TerrainRaycast.Cast(Terrain.Map, start, direction, length) is { } ground)
        {
            nearest = ground.Distance;
            hit = new VisualHit(ground.Point, ground.Normal, Flesh: false);
        }

        foreach (var obj in Objects.Objects)
        {
            if (!obj.Has.HasFlag(NetComponents.Transform)
                || GunMath.HitDistance(start, direction, obj.Transform.Position, length) is not { } t
                || t >= nearest)
                continue;

            nearest = t;
            hit = new VisualHit(start + direction * t, Normal: null, Flesh: false);
        }

        foreach (var player in Registry.Players)
        {
            if (player.Id == shooterId) continue;
            var center = player.Position
                + new System.Numerics.Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
            if (GunMath.HitDistance(start, direction, center, length) is not { } t || t >= nearest)
                continue;

            nearest = t;
            hit = new VisualHit(start + direction * t, -direction, Flesh: true);
        }

        return nearest < float.MaxValue;
    }

    private static void DrawSegment(
        System.Numerics.Vector3 start,
        System.Numerics.Vector3 end,
        Color color)
    {
        if (System.Numerics.Vector3.DistanceSquared(start, end)
            < MinTracerLength * MinTracerLength)
            return;
        TracerManager.Spawn(start.ToStride(), end.ToStride(), color, TracerLifetime);
    }

    private static bool IsFinite(System.Numerics.Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
