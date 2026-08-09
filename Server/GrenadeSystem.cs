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
               && ItemCatalog.HasBehavior(item.Item.Type, ItemBehavior.Grenade);

        // Legacy tests/admin equips place their grenade directly in Hand.
        slot = EquipSlot.Hand;
        return player.Hotbar == HotbarSlot.Primary
           && player.Equipped.TryGetValue(slot, out uint itemId)
           && objects.TryGet(itemId, out item)
           && item.Has.HasFlag(NetComponents.Item | NetComponents.Weapon)
           && ItemCatalog.HasBehavior(item.Item.Type, ItemBehavior.Grenade);
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
                tick + (uint)WeaponConfig.Require(ItemCatalog.RequireBehavior(ItemBehavior.Grenade)).ReloadTicks;
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
        // Hole first, then casualties. The blast has to see a man to hurt him, so the wall it just
        // blew through must be gone by the time anyone is checked against it.
        Crater(terrainEdits, terrain, grenade.Position, GrenadeConfig.Blast);

        ApplyBlastDamage(
            terrain,
            grenade.Position,
            players,
            tick,
            GrenadeConfig.Blast,
            victim => activityFeed?.ReportKill(grenade.Owner, victim));

        // Same reason as the mortar: the fuse expires mid-tick, so a grenade still in the air has
        // moved since its last broadcast and the burst belongs where it actually went off.
        grenade.Object.Transform.Position = grenade.Position;
        objects.Despawn(grenade.Object.NetworkId);
    }

    /// <summary>
    /// Digs the hole a blast leaves, under whatever it went off above.
    ///
    /// Shared with the mortar rather than reimplemented for it, and the profile is what makes that
    /// possible: the footprint is a readable 2x2 of bites and the profile scales how hard each one
    /// bites, so a bigger bang leaves a bigger hole without a second copy of this loop drifting away
    /// from the first.
    /// </summary>
    internal static void Crater(
        TerrainSystem terrainEdits,
        ChunkMap terrain,
        Vector3 origin,
        in BlastProfile blast)
    {
        var contact = TerrainRaycast.Cast(terrain, origin, -Vector3.UnitY, blast.DamageRadius);
        if (contact is not { } terrainContact) return;

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
                    Mode = EditMode.SubtractBlast,
                    Fill = BlockType.BlockType_Air,
                    Shape = EditShape.Sphere,
                    Strength = Digging.BiteStrength * blast.TerrainDeformationScale,
                });
            }
        }
    }

    /// <summary>
    /// Applies radial damage to every actor the blast can both reach and see, including the thrower
    /// and other friendly actors. There is deliberately no team or owner exclusion: grenade friendly
    /// fire is always enabled.
    ///
    /// Range alone used to be the whole test, which killed men through walls and floors — the one
    /// thing a trench is for. Terrain now blocks a blast the way it already blocked a bullet.
    /// </summary>
    internal static void ApplyBlastDamage(
        ChunkMap terrain,
        Vector3 origin,
        IEnumerable<ServerPlayer> players,
        uint tick,
        in BlastProfile blast,
        Action<ServerPlayer>? killed = null)
    {
        foreach (var player in players)
        {
            if (player.Status is not { } status || status.Health.Current == 0) continue;

            bool wasAlive = status.Health.Current > 0;
            float distance = Vector3.Distance(origin, player.Position);
            float fraction = blast.DamageFraction(distance);
            if (fraction <= 0f) continue;

            // Cast only for those the falloff has already admitted: the rays are the expensive part
            // and most of the world is out of range of any given bang.
            if (!HasLineOfSight(terrain, origin, player)) continue;

            // The blow's own magnitude, which inside the lethal radius is everything a man has even
            // though the health assignment below does not go through a damage number. The ragdoll
            // shove scales off this rather than off health removed, so a wounded man and a whole one
            // are thrown the same distance by the same grenade.
            float blowDamage = status.Health.Max * fraction;

            if (distance <= blast.LethalRadius)
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
            {
                // Out from the blast through the body's middle, not its feet: an explosion on the
                // ground beside a man should tip him over, and origin-to-centre says that by itself.
                var centre = player.Position + new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
                status.Impulse.Velocity = RagdollImpulse.FromBlast(origin, centre, blowDamage);
                status.Dirty |= NetComponents.Impulse;

                killed?.Invoke(player);
            }
        }
    }

    /// <summary>
    /// How far off the origin a sight ray starts. A bomb detonates ON a surface, and the crossing
    /// <see cref="TerrainRaycast"/> reports can sit a hundredth of a voxel inside it — from there
    /// every ray hits at once and the blast harms nobody. Stepping clear costs nothing in open air
    /// and still reports the wall when the man is on the far side of one.
    /// </summary>
    private const float SightOriginClearance = 0.05f;

    /// <summary>
    /// How far above the impact point the second set of rays starts.
    ///
    /// A blast is not a point on the floor. It goes off ON the surface it landed on, so a ray from
    /// exactly there runs along the ground and is stopped by the first tussock or lip it grazes —
    /// which made a grenade in a shallow dip harmless to a man standing beside it, and a bomb
    /// bursting against a forward slope harmless to everything behind the crest of it. Half a metre
    /// up is roughly where the fireball and the fragments actually come from.
    ///
    /// It is an ADDITIONAL origin rather than a replacement: raising the only origin would let a
    /// charge in a tunnel or under an overhang see through the roof it is pressed against. Either
    /// origin reaching a man is enough, so the pair is self-correcting — whichever one is buried
    /// simply contributes nothing.
    /// </summary>
    private const float BurstRise = 0.5f;

    /// <summary>
    /// Whether the blast can see the man at all: two origins by three body points, and any one of
    /// the six arriving is enough.
    ///
    /// One body point cannot answer this, and which one you pick only chooses which mistake to make.
    /// To the feet alone shelters a man whose head is over the parapet; to the head alone shelters
    /// one lying behind a berm with his legs in the open. Feet, centre mass and head together mean
    /// cover has to actually cover him.
    /// </summary>
    internal static bool HasLineOfSight(ChunkMap terrain, Vector3 origin, ServerPlayer player)
    {
        var feet = player.Position;
        bool crouching = player.State.HasFlag(PlayerStateFlags.Crouching);
        var centre = feet + new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
        var head = GunConfig.HeadCenter(feet, crouching);
        var burst = origin + new Vector3(0f, BurstRise, 0f);

        // Raised origin first and centre mass first within each, which is the order they are most
        // likely to arrive in — so the common case answers on one ray rather than six.
        return Reaches(terrain, burst, centre)
            || Reaches(terrain, burst, head)
            || Reaches(terrain, burst, feet)
            || Reaches(terrain, origin, centre)
            || Reaches(terrain, origin, head)
            || Reaches(terrain, origin, feet);
    }

    private static bool Reaches(ChunkMap terrain, Vector3 origin, Vector3 target)
    {
        var segment = target - origin;
        float distance = segment.Length();
        if (distance <= SightOriginClearance) return true;

        var direction = segment / distance;
        return TerrainRaycast.Cast(
            terrain,
            origin + direction * SightOriginClearance,
            direction,
            distance - SightOriginClearance) is null;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z);
}
