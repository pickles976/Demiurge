using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Server-authoritative placeholder grenades. Throws reuse the existing fire request, while the
/// grenade itself is an ordinary replicated transform object. Its release-started fuse and swept,
/// damped terrain bounces make the same throw reproducible at every server tick rate.
/// </summary>
public sealed class GrenadeSystem
{
    private sealed class ActiveGrenade
    {
        public required ServerObject Object { get; init; }
        public required Vector3 Position { get; set; }
        public required Vector3 Velocity { get; set; }
        public required uint SpawnTick { get; init; }
        public required uint DetonateTick { get; init; }
        public required ServerPlayer Owner { get; init; }
        public bool Resting { get; set; }
    }

    private readonly ObjectReplication objects;
    private readonly ItemSystem items;
    private readonly TerrainSystem terrainEdits;
    private readonly ChunkMap terrain;
    private readonly ActivityFeedSystem? activityFeed;
    private readonly List<ActiveGrenade> active = [];

    public GrenadeSystem(
        ObjectReplication objects,
        ItemSystem items,
        TerrainSystem terrainEdits,
        ChunkMap terrain,
        ActivityFeedSystem? activityFeed = null)
    {
        this.objects = objects;
        this.items = items;
        this.terrainEdits = terrainEdits;
        this.terrain = terrain;
        this.activityFeed = activityFeed;
    }

    public bool IsGrenadeEquipped(ServerPlayer player)
        => TryGetGrenade(player, out _, out _);

    public bool CanThrow(ServerPlayer player, uint tick)
        => TryGetGrenade(player, out _, out var item)
           && item.Weapon.CurrentAmmo > 0
           && tick >= player.NextGrenadeThrowTick;

    private bool TryGetGrenade(
        ServerPlayer player,
        out EquipSlot slot,
        out ServerObject item)
    {
        item = null!;
        slot = EquipSlot.HotbarGrenade;
        if (player.Hotbar == HotbarSlot.Grenade)
            return player.Equipped.TryGetValue(slot, out uint grenadeId)
               && objects.TryGet(grenadeId, out item)
               && item.Has.HasFlag(NetComponents.Item | NetComponents.Weapon)
               && item.Item.Type == ItemType.Grenade;

        // Legacy tests/admin equips place their grenade directly in Hand.
        slot = EquipSlot.Hand;
        return player.Hotbar == HotbarSlot.Primary
           && player.Equipped.TryGetValue(slot, out uint itemId)
           && objects.TryGet(itemId, out item)
           && item.Has.HasFlag(NetComponents.Item | NetComponents.Weapon)
           && item.Item.Type == ItemType.Grenade;
    }

    public bool ApplyThrow(ServerPlayer player, PlayerFireData fire, uint tick)
    {
        if (!IsFinite(fire.Origin)
            || !IsFinite(fire.Direction)
            || !float.IsFinite(fire.RenderTick)
            || fire.RenderTick > tick
            || fire.RenderTick < (double)tick - NetworkConfig.MaxRewindTicks
            || fire.Direction.LengthSquared() < 1e-8f)
            return false;

        if (!TryGetGrenade(player, out var slot, out var item)
            || item.Weapon.CurrentAmmo <= 0
            || tick < player.NextGrenadeThrowTick)
            return false;

        if (Vector3.DistanceSquared(fire.Origin, player.Position)
            > GunConfig.MaxFireOriginDistance * GunConfig.MaxFireOriginDistance)
            return false;

        item.Weapon.CurrentAmmo--;
        if (item.Weapon.CurrentAmmo == 0)
        {
            if (!items.ConsumeEquipped(player, slot, item.NetworkId)) return false;
        }
        else
        {
            item.Dirty |= NetComponents.Weapon;
            player.NextGrenadeThrowTick =
                tick + (uint)WeaponConfig.Require(ItemType.Grenade).ReloadTicks;
        }

        var direction = Vector3.Normalize(fire.Direction);
        var grenadeObject = objects.Spawn(
            ObjectType.Grenade,
            NetComponents.Transform,
            fire.Origin);
        active.Add(new ActiveGrenade
        {
            Object = grenadeObject,
            Position = fire.Origin,
            Velocity = direction * GrenadeConfig.ThrowSpeed,
            SpawnTick = tick,
            DetonateTick = tick + GrenadeConfig.FuseTicks,
            Owner = player,
        });
        return true;
    }

    public void Tick(float dt, uint tick, IEnumerable<ServerPlayer> players)
    {
        uint maxFlightTicks =
            (uint)MathF.Ceiling(GrenadeConfig.MaxFlightSeconds * NetworkConfig.TickRate);

        for (int i = active.Count - 1; i >= 0; i--)
        {
            var grenade = active[i];
            if (tick >= grenade.DetonateTick)
            {
                Detonate(grenade, tick, players);
                active.RemoveAt(i);
                continue;
            }

            if (tick - grenade.SpawnTick >= maxFlightTicks)
            {
                objects.Despawn(grenade.Object.NetworkId);
                active.RemoveAt(i);
                continue;
            }

            if (!grenade.Resting)
                Advance(grenade, dt);

            grenade.Object.Transform.Position = grenade.Position;
            grenade.Object.Dirty |= NetComponents.Transform;
        }
    }

    private void Advance(ActiveGrenade grenade, float dt)
    {
        float remaining = dt;
        var gravity = new Vector3(0f, -GrenadeConfig.Gravity, 0f);

        for (int impact = 0;
             impact < GrenadeConfig.MaxImpactsPerTick && remaining > 1e-5f;
             impact++)
        {
            var end = grenade.Position
                + grenade.Velocity * remaining
                + 0.5f * gravity * remaining * remaining;
            var segment = end - grenade.Position;
            float length = segment.Length();
            if (length <= 1e-6f)
            {
                grenade.Velocity += gravity * remaining;
                break;
            }

            var hit = TerrainRaycast.Cast(
                terrain,
                grenade.Position,
                segment / length,
                length);
            if (hit is not { } contact)
            {
                grenade.Position = end;
                grenade.Velocity += gravity * remaining;
                break;
            }

            float fraction = Math.Clamp(contact.Distance / length, 0f, 1f);
            float hitTime = remaining * fraction;
            var velocityAtHit = grenade.Velocity + gravity * hitTime;
            grenade.Position = contact.Point
                + contact.Normal * (GrenadeConfig.Radius + GrenadeConfig.SurfaceOffset);
            grenade.Velocity = BounceVelocity(velocityAtHit, contact.Normal);

            float incomingNormalSpeed = MathF.Max(
                0f,
                -Vector3.Dot(velocityAtHit, contact.Normal));
            bool floor = contact.Normal.Y >= GrenadeConfig.GroundNormalThreshold;
            if (floor
                && incomingNormalSpeed <= GrenadeConfig.RestNormalSpeed
                && grenade.Velocity.Length() <= GrenadeConfig.RestSpeed)
            {
                grenade.Velocity = Vector3.Zero;
                grenade.Resting = true;
                break;
            }

            remaining -= hitTime;
            // A contact at the start of a sweep can otherwise consume no time and repeatedly hit
            // the same surface. The offset prevents the usual case; this guarantees termination.
            if (hitTime <= 1e-5f)
                remaining = MathF.Max(0f, remaining - 1e-4f);
        }
    }

    internal static Vector3 BounceVelocity(Vector3 incoming, Vector3 normal)
    {
        normal = Vector3.Normalize(normal);
        float normalSpeed = Vector3.Dot(incoming, normal);
        if (normalSpeed >= 0f) return incoming;

        var normalVelocity = normal * normalSpeed;
        var tangentVelocity = incoming - normalVelocity;
        float restitution = normal.Y >= GrenadeConfig.GroundNormalThreshold
            ? GrenadeConfig.GroundRestitution
            : GrenadeConfig.WallRestitution;
        return tangentVelocity * GrenadeConfig.TangentialRetention
             - normalVelocity * restitution;
    }

    private void Detonate(ActiveGrenade grenade, uint tick, IEnumerable<ServerPlayer> players)
    {
        ApplyBlastDamage(
            grenade.Position,
            players,
            tick,
            victim => activityFeed?.ReportKill(grenade.Owner, victim));

        // Keep the readable 2x2 footprint, but apply each bite at half strength so the total
        // deformation is half of the original four-click crater.
        var contact = TerrainRaycast.Cast(
            terrain,
            grenade.Position,
            -Vector3.UnitY,
            GrenadeConfig.DamageRadius);
        if (contact is { } terrainContact)
        {
            var target = Digging.TargetVoxel(terrainContact.Point, terrainContact.Normal);
            float firstX = MathF.Floor(target.X - 0.5f);
            float firstZ = MathF.Floor(target.Z - 0.5f);
            for (int z = 0; z < 2; z++)
            {
                for (int x = 0; x < 2; x++)
                {
                    terrainEdits.Apply(new TerrainEditData
                    {
                        Centre = new Vector3(firstX + x, target.Y, firstZ + z),
                        HalfExtent = Digging.Bite,
                        Mode = EditMode.SubtractSoil,
                        Fill = BlockType.BlockType_Air,
                        Shape = EditShape.Sphere,
                        Strength =
                            Digging.BiteStrength * GrenadeConfig.TerrainDeformationScale,
                    });
                }
            }
        }
        objects.Despawn(grenade.Object.NetworkId);
    }

    /// <summary>
    /// Applies radial damage to every actor, including the thrower and other friendly actors.
    /// There is deliberately no team or owner exclusion: grenade friendly fire is always enabled.
    /// </summary>
    internal static void ApplyBlastDamage(
        Vector3 origin,
        IEnumerable<ServerPlayer> players,
        uint tick,
        Action<ServerPlayer>? killed = null)
    {
        foreach (var player in players)
        {
            if (player.Status is not { } status || status.Health.Current == 0) continue;

            bool wasAlive = status.Health.Current > 0;
            float distance = Vector3.Distance(origin, player.Position);
            float fraction = GrenadeConfig.DamageFraction(distance);
            if (fraction <= 0f) continue;

            if (distance <= GrenadeConfig.LethalRadius)
            {
                status.Health.Current = 0;
            }
            else
            {
                int maximumNonLethal = Math.Max(1, status.Health.Max - 1);
                ushort damage = (ushort)Math.Clamp(
                    (int)MathF.Round(status.Health.Max * fraction),
                    1,
                    maximumNonLethal);
                status.Health.Current = status.Health.Current > damage
                    ? (ushort)(status.Health.Current - damage)
                    : (ushort)0;
            }
            player.LastDamagedTick = tick;
            status.Dirty |= NetComponents.Health;
            if (wasAlive && status.Health.Current == 0)
                killed?.Invoke(player);
        }
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z);
}
