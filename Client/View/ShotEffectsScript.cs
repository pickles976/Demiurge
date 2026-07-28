using Demiurge;
using Demiurge.GameClient;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

// Shot feedback: tracer + positional audio, per weapon (ItemCosmetics).
// Two inputs, one effects path:
//  - local player: the sim's ShotFired event, instantly on the PREDICTED shot;
//  - remote players: the server's PlayerFired broadcast (accepted shots only),
//    filtered to skip our own id so predicted shots never double-flash.
// View-only — the server's raycast decides real hits; the tracer endpoint just
// mirrors the same math (GunMath) over the replicated objects.
public class ShotEffectsScript : SyncScript
{
    public required PlayerRegistry Registry { get; init; }
    public required ObjectRegistry Objects { get; init; }
    public required NetworkManager Network { get; init; }
    public required TerrainState Terrain { get; init; }

    private const float TracerLifetime = 0.1f;   // the old GunScript's tracer look
    private const float MinTracerLength = 0.03f;

    /// <summary>Dust, not sparks: pale and a little transparent, so a burst of them reads as one
    /// kicked-up cloud rather than seven separate lines.</summary>
    private static readonly Color ImpactColor = new(214, 198, 172, 205);
    private static readonly Color FleshImpactColor = new(222, 44, 44, 230);
    private static readonly Color DamageTextColor = new(255, 64, 64, 255);

    private SoundManager sound = null!;
    private LocalPlayer? subscribed;

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
    }

    private void OnLocalShot(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction)
    {
        if (subscribed is not { IsArmed: true } local) return;   // ShotFired implies armed, but be safe
        PlayEffects(origin, direction, local.Weapon!.Item.Type, local.Stats.MaxRange, Network.ClientId);
    }

    private void OnRemoteFired(PlayerFiredData data)
    {
        if (data.PlayerId == Network.ClientId) return;   // our shots already played predictively
        // Off-the-wire type: soft-fail to a plausible range — a wrong tracer
        // length is cosmetic, a crash is not.
        PlayEffects(data.Origin, data.Direction, data.Weapon, WeaponConfig.Get(data.Weapon)?.MaxRange ?? 100f, data.PlayerId);
    }

    private void OnHitConfirmed(HitConfirmData confirm)
    {
        if (!Objects.TryGet(confirm.TargetNetworkId, out var target)) return;
        if (!target.Has.HasFlag(NetComponents.Owner)) return;
        if (!Registry.TryGet(target.Owner.PlayerId, out var player)) return;

        var position = player.Position + new System.Numerics.Vector3(0f, GunConfig.PlayerCenterHeight + 0.6f, 0f);
        DamageTextManager.Spawn(position.ToStride(), confirm.Damage, DamageTextColor);
    }

    private void PlayEffects(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, ItemType weapon, float maxRange, ushort shooterId)
    {
        if (!IsFinite(origin) || !IsFinite(direction) || direction.LengthSquared() < 1e-8f || maxRange <= 0f)
            return;
        direction = System.Numerics.Vector3.Normalize(direction);

        // End the tracer at the nearest replicated object the ray passes within
        // HitRadius of — the same test the server runs — or at max range.
        float distance = maxRange;
        foreach (var obj in Objects.Objects)
            if (obj.Has.HasFlag(NetComponents.Transform)   // equipped weapons have no world position
                && GunMath.HitDistance(origin, direction, obj.Transform.Position, maxRange) is { } t
                && t < distance)
                distance = t;

        System.Numerics.Vector3? bodyImpact = null;
        foreach (var player in Registry.Players)
        {
            if (player.Id == shooterId) continue;
            var center = player.Position + new System.Numerics.Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
            if (GunMath.HitDistance(origin, direction, center, maxRange) is not { } t || t >= distance)
                continue;

            distance = t;
            bodyImpact = origin + direction * t;
        }

        // Terrain stops the shot if it gets there first, matching what the server decides — it runs
        // this same cast before awarding a hit, so a tracer that buries itself in a hillside is
        // showing you a shot that really was stopped, not just a shortened line.
        TerrainHit? ground = TerrainRaycast.Cast(Terrain.Map, origin, direction, distance);
        if (ground is { } g)
        {
            distance = g.Distance;
            bodyImpact = null;
        }

        var fx = WeaponFx.Get(weapon);
        var start = origin.ToStride();
        sound.PlayOneShotSpatial(fx.ShotSoundPath, start);

        // A barrel can be visibly buried in terrain, especially inside a trench. TerrainRaycast then
        // returns a valid hit at distance zero. Play the shot and impact, but do not feed a zero-length
        // tracer into the line renderer path.
        if (distance >= MinTracerLength)
        {
            var end = (origin + direction * distance).ToStride();
            TracerManager.Spawn(start, end, fx.TracerColor, TracerLifetime);
        }

        // Debris only where there is ground to kick up — a shot into the sky or into a player
        // leaves nothing behind.
        if (ground is { } hit) ImpactManager.Spawn(hit.Point.ToStride(), hit.Normal.ToStride(), ImpactColor);
        else if (bodyImpact is { } body) ImpactManager.Spawn(body.ToStride(), (-direction).ToStride(), FleshImpactColor);
    }

    private static bool IsFinite(System.Numerics.Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
