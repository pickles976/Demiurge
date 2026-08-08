using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Bombs in the air, and the emplacements that put them there.
///
/// Separate from <see cref="GrenadeSystem"/> despite the shared ending, because the flight is a
/// different thing entirely: a grenade is thrown along a direction, bounces, and goes off on a fuse,
/// while a bomb is lobbed AT A PLACE, does not bounce, and goes off when it arrives. What the two
/// share is the bang, and that is shared literally — both hand a <see cref="BlastProfile"/> to
/// <see cref="GrenadeSystem.ApplyBlastDamage"/> rather than owning a copy of the falloff.
/// </summary>
public sealed class MortarSystem
{
    private sealed class Bomb
    {
        public required ServerObject Object { get; init; }
        public required ServerPlayer Owner { get; init; }
        public required Vector3 Target { get; init; }
        public required uint SpawnTick { get; init; }
        public Vector3 Position { get; set; }
        public Vector3 Velocity { get; set; }
    }

    private readonly ObjectReplication objects;
    private readonly ChunkMap terrain;
    private readonly TerrainSystem terrainEdits;
    private readonly ActivityFeedSystem? activityFeed;
    private readonly List<Bomb> inFlight = [];

    /// <summary>Server-side only, and deliberately not seeded from anything a client sends: where a
    /// bomb lands must not be predictable from the request that fired it.</summary>
    private readonly Random dispersion = new();

    public MortarSystem(
        ObjectReplication objects,
        ChunkMap terrain,
        TerrainSystem terrainEdits,
        ActivityFeedSystem? activityFeed = null)
    {
        this.objects = objects;
        this.terrain = terrain;
        this.terrainEdits = terrainEdits;
        this.activityFeed = activityFeed;
    }

    internal int InFlight => inFlight.Count;

    /// <summary>
    /// Fires the mortar this player is operating at <paramref name="target"/>, or refuses.
    ///
    /// Every reason to refuse is checked against the SERVER's copy of the emplacement, never against
    /// anything the request carried: which mortar he is on, where it sits, which way it was laid,
    /// and whether the tube is loaded. The request contributes one thing — a point — and even that
    /// is re-clamped rather than trusted.
    /// </summary>
    public bool TryFire(ServerPlayer gunner, ServerObject mortar, Vector3 target, uint tick)
    {
        if (tick < gunner.ReloadDoneTick) return false;
        if (!MortarBallistics.IsLegalTarget(mortar.Transform.Position, mortar.Transform.Yaw, target))
            return false;

        // Aimed, then scattered. The gunner picks a point; the bomb goes somewhere near it, and the
        // solution is computed for where it is ACTUALLY going so the arc and the impact agree.
        var aimed = MortarBallistics.ClampTarget(mortar.Transform.Position, mortar.Transform.Yaw, target);
        var landing = MortarBallistics.Disperse(aimed, dispersion);
        var muzzle = mortar.Transform.Position + Vector3.UnitY * MortarBallistics.MuzzleHeight;
        if (MortarBallistics.SolveVelocity(muzzle, landing, ProjectileMotion.Gravity) is not { } velocity)
            return false;

        // The tube is empty until it is loaded again — five seconds during which the gunner is doing
        // nothing else, which is the cost of the weapon.
        gunner.ReloadDoneTick = tick + (uint)WeaponConfig.Require(ItemType.Mortar).ReloadTicks;

        var bomb = objects.Spawn(ObjectType.MortarRound, NetComponents.Transform, muzzle);
        inFlight.Add(new Bomb
        {
            Object = bomb,
            Owner = gunner,
            Target = landing,
            SpawnTick = tick,
            Position = muzzle,
            Velocity = velocity,
        });
        return true;
    }

    public void Tick(float dt, uint tick, IEnumerable<ServerPlayer> players)
    {
        uint maxFlightTicks = (uint)MathF.Ceiling(MortarConfig.MaxFlightSeconds * NetworkConfig.TickRate);

        for (int i = inFlight.Count - 1; i >= 0; i--)
        {
            var bomb = inFlight[i];

            if (tick - bomb.SpawnTick >= maxFlightTicks)
            {
                objects.Despawn(bomb.Object.NetworkId);
                inFlight.RemoveAt(i);
                continue;
            }

            var start = bomb.Position;
            bomb.Velocity -= Vector3.UnitY * ProjectileMotion.Gravity * dt;
            bomb.Position += bomb.Velocity * dt;

            // Two ways to arrive, and both are "it got there". The swept terrain cast is what stops
            // a fast bomb tunnelling through a hillside between ticks; the descent test is what
            // makes it go off at the aimed height over ground the cast cannot see, so a round aimed
            // into a trench detonates in the trench rather than sailing on.
            var segment = bomb.Position - start;
            float distance = segment.Length();
            Vector3? impact = null;

            if (distance > 1e-5f
                && TerrainRaycast.Cast(terrain, start, segment / distance, distance) is { } ground)
                impact = ground.Point;
            else if (bomb.Velocity.Y < 0f && bomb.Position.Y <= bomb.Target.Y)
                impact = bomb.Target;

            if (impact is { } where)
            {
                Detonate(bomb, where, tick, players);
                inFlight.RemoveAt(i);
                continue;
            }

            bomb.Object.Transform.Position = bomb.Position;
            bomb.Object.Dirty |= NetComponents.Transform;
        }
    }

    private void Detonate(Bomb bomb, Vector3 where, uint tick, IEnumerable<ServerPlayer> players)
    {
        objects.Despawn(bomb.Object.NetworkId);

        GrenadeSystem.ApplyBlastDamage(
            where,
            players,
            tick,
            MortarConfig.Blast,
            victim => activityFeed?.ReportKill(bomb.Owner, victim));

        GrenadeSystem.Crater(terrainEdits, terrain, where, MortarConfig.Blast);
    }
}
