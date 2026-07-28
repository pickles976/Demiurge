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

    /// <summary>Dust, not sparks: pale and a little transparent, so a burst of them reads as one
    /// kicked-up cloud rather than seven separate lines.</summary>
    private static readonly Color ImpactColor = new(214, 198, 172, 205);

    private SoundManager sound = null!;
    private LocalPlayer? subscribed;

    public override void Start()
    {
        sound = Services.GetSafeServiceAs<SoundManager>();
        Network.PlayerFired += OnRemoteFired;
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
        PlayEffects(origin, direction, local.Weapon!.Item.Type, local.Stats.MaxRange);
    }

    private void OnRemoteFired(PlayerFiredData data)
    {
        if (data.PlayerId == Network.ClientId) return;   // our shots already played predictively
        // Off-the-wire type: soft-fail to a plausible range — a wrong tracer
        // length is cosmetic, a crash is not.
        PlayEffects(data.Origin, data.Direction, data.Weapon, WeaponConfig.Get(data.Weapon)?.MaxRange ?? 100f);
    }

    private void PlayEffects(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, ItemType weapon, float maxRange)
    {
        // End the tracer at the nearest replicated object the ray passes within
        // HitRadius of — the same test the server runs — or at max range.
        float distance = maxRange;
        foreach (var obj in Objects.Objects)
            if (obj.Has.HasFlag(NetComponents.Transform)   // equipped weapons have no world position
                && GunMath.HitDistance(origin, direction, obj.Transform.Position, maxRange) is { } t
                && t < distance)
                distance = t;

        // Terrain stops the shot if it gets there first, matching what the server decides — it runs
        // this same cast before awarding a hit, so a tracer that buries itself in a hillside is
        // showing you a shot that really was stopped, not just a shortened line.
        TerrainHit? ground = TerrainRaycast.Cast(Terrain.Map, origin, direction, distance);
        if (ground is { } g) distance = g.Distance;

        var fx = WeaponFx.Get(weapon);
        var start = origin.ToStride();
        var end = (origin + direction * distance).ToStride();
        TracerManager.Spawn(start, end, fx.TracerColor, TracerLifetime);
        sound.PlayOneShotSpatial(fx.ShotSoundPath, start);

        // Debris only where there is ground to kick up — a shot into the sky or into a player
        // leaves nothing behind.
        if (ground is { } hit) ImpactManager.Spawn(hit.Point.ToStride(), hit.Normal.ToStride(), ImpactColor);
    }
}
