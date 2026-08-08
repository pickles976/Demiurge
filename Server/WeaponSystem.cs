using Demiurge.Net;
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>Server-authoritative fire/reload validation — the one weapon-
    /// specific system. Pickup/equip/swap belong to ItemSystem; weapons live in
    /// ObjectReplication like every item; timing gates live on ServerPlayer.
    /// GameWorld resolves clientId -> ServerPlayer and delegates.</summary>
    public class WeaponSystem
    {
        internal readonly record struct AcceptedGunshot(
            ushort ShooterId,
            int ShooterTeam,
            Vector3 Position,
            uint Tick);

        internal readonly record struct AcceptedSuppression(
            ushort TargetId,
            ushort ShooterId,
            int ShooterTeam,
            Vector3 ThreatPosition,
            uint Tick);

        private sealed class Projectile
        {
            public required ServerPlayer Shooter { get; init; }
            public required Vector3 Origin { get; init; }
            public required Vector3 Position { get; set; }
            public required Vector3 Velocity { get; set; }
            public required float RemainingDistance { get; set; }
            public required ushort Damage { get; init; }
            public HashSet<ushort> SuppressedActors { get; } = [];
        }

        private readonly INetServer server;
        private readonly ObjectReplication objects;
        private readonly ChunkMap terrain;
        private readonly ActivityFeedSystem? activityFeed;
        private readonly List<Projectile> projectiles = new();
        private readonly Queue<AcceptedGunshot> gunshots = new();
        private readonly Queue<AcceptedSuppression> suppressions = new();

        public WeaponSystem(
            INetServer server,
            ObjectReplication objects,
            ChunkMap terrain,
            ActivityFeedSystem? activityFeed = null)
        {
            this.server = server;
            this.objects = objects;
            this.terrain = terrain;
            this.activityFeed = activityFeed;
        }

        public void ApplyFire(ServerPlayer player, PlayerFireData fire, uint tick)
        {
            if (!IsFinite(fire.Origin) || !IsFinite(fire.Direction) || !float.IsFinite(fire.RenderTick)) return;

            // Reject views from the future or older than max history
            if (fire.RenderTick > tick || fire.RenderTick < (double)tick - NetworkConfig.MaxRewindTicks) return;
            if (fire.Direction.LengthSquared() < 1e-8f) return;

            // The client supplies the aim, but the shot must leave from roughly where
            // the server has the player. See GunConfig.MaxFireOriginDistance for what the
            // tolerance has to cover — a barrel swung to full pitch reaches further than it looks.
            if (Vector3.DistanceSquared(fire.Origin, player.Position)
                > GunConfig.MaxFireOriginDistance * GunConfig.MaxFireOriginDistance) return;

            TryFireCore(player, fire.Origin, fire.Direction, tick, fire.Sequence, additionalMoa: 0f);
        }

        internal bool TryFireAi(
            ServerPlayer player,
            Vector3 origin,
            Vector3 direction,
            uint tick,
            uint sequence,
            float additionalMoa)
        {
            if (!IsFinite(origin) || !IsFinite(direction) || direction.LengthSquared() < 1e-8f)
                return false;
            return TryFireCore(player, origin, direction, tick, sequence, additionalMoa);
        }

        private bool TryFireCore(
            ServerPlayer player,
            Vector3 origin,
            Vector3 requestedDirection,
            uint tick,
            uint sequence,
            float additionalMoa)
        {
            if (!TryGetActiveWeapon(player, out var weapon)) return false;

            // Freight in the hands is not a weapon in them. A carryable is used once it is set
            // down, so the ordinary fire path refuses it — otherwise a mortar somebody is hauling
            // would work, and work WRONGLY, as a flat rifle shot out of a carried tube. Asked of the
            // CATEGORY so the next heavy thing inherits the rule instead of being named here.
            if (ItemConfig.IsCarryable(weapon.Item.Type)) return false;

            var stats = WeaponConfig.Require(weapon.Item.Type);
            if (tick < player.NextFireTick
                || tick < player.ReloadDoneTick
                || weapon.Weapon.CurrentAmmo <= 0)
                return false;

            // Retain a deadline that is at most one tick behind so fractional rates preserve their
            // phase (1.5 ticks alternates 2/1), but an idle weapon cannot bank a magazine of shots.
            player.NextFireTick = player.NextFireTick < tick - 1f
                ? tick + stats.TicksPerShot
                : player.NextFireTick + stats.TicksPerShot;
            weapon.Weapon.CurrentAmmo--;
            weapon.Dirty |= NetComponents.Weapon;       // ammo replicates like any component

            var ballistics = BallisticsConfig.Require(weapon.Item.Type);
            float moa = Spread.Combine(
                player.Spread.TotalMoa(player.State, ballistics),
                MathF.Max(0f, additionalMoa));
            var direction = Spread.SampleDirection(
                requestedDirection,
                Spread.SigmaRadians(moa),
                Spread.ShotSeed(player.Id, sequence));

            player.Spread.AddRecoil(ballistics, player.State);
            gunshots.Enqueue(new AcceptedGunshot(
                player.Id,
                player.Team,
                origin,
                tick));
            projectiles.Add(new Projectile
            {
                Shooter = player,
                Origin = origin,
                Position = origin,
                Velocity = direction * ballistics.ProjectileSpeed,
                RemainingDistance = ProjectileMotion.SafetyDistance,
                Damage = stats.Damage,
            });

            // Cosmetic rebroadcast for remote tracers/audio. Unreliable: a lost
            // tracer is nothing. Only ACCEPTED shots get here, so rejected fire
            // never flashes on anyone's screen.
            Message fired = Message.Create(MessageSendMode.Unreliable, ServerToClientId.PlayerFired);
            fired.AddSerializable(new PlayerFiredData
            {
                PlayerId = player.Id,
                Weapon = weapon.Item.Type,
                Origin = origin,
                Direction = direction,
            });
            server.SendToAll(fired);
            return true;
        }

        /// <summary>
        /// Rounds that strike near a man suppress him even though they never passed near him. The
        /// fly-by test in TryHit measures distance from the projectile's PATH, so fire aimed at the
        /// cover someone is behind — which is what suppressing fire IS — went entirely unnoticed by
        /// the person being suppressed.
        /// </summary>
        private void SuppressNearImpact(
            Projectile projectile,
            Vector3 impact,
            IEnumerable<ServerPlayer> actors,
            uint tick)
        {
            foreach (var player in actors)
            {
                if (player == projectile.Shooter
                    || player.Team == projectile.Shooter.Team
                    || player.Status is not { Health.Current: > 0 })
                    continue;

                var center = player.Position + new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
                if (Vector3.DistanceSquared(impact, center)
                    > GunConfig.ImpactSuppressionRadius * GunConfig.ImpactSuppressionRadius)
                    continue;

                player.Spread.Suppress();
                if (!projectile.SuppressedActors.Add(player.Id)) continue;
                suppressions.Enqueue(new AcceptedSuppression(
                    player.Id,
                    projectile.Shooter.Id,
                    projectile.Shooter.Team,
                    projectile.Origin,
                    tick));
            }
        }

        /// <summary>How many projectiles are in flight, for the tick breakdown.</summary>
        internal int LiveProjectiles => projectiles.Count;

        internal bool TryDequeueGunshot(out AcceptedGunshot gunshot)
            => gunshots.TryDequeue(out gunshot);

        internal bool TryDequeueSuppression(out AcceptedSuppression suppression)
            => suppressions.TryDequeue(out suppression);

        /// <summary>
        /// Advances shooter dispersion and every live projectile once. Collision is swept over
        /// the whole tick segment, so a fast rifle bullet cannot tunnel through a target between
        /// two 30 Hz updates.
        /// </summary>
        public void Tick(float dt, uint tick, IEnumerable<ServerPlayer> players)
        {
            var actors = players as ICollection<ServerPlayer> ?? players.ToArray();
            foreach (var player in actors)
            {
                if (!TryGetActiveWeapon(player, out var weapon))
                    continue;

                player.Spread.Advance(
                    player.State,
                    BallisticsConfig.Require(weapon.Item.Type),
                    dt);
            }

            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                var projectile = projectiles[i];
                var step = ProjectileMotion.Advance(
                    projectile.Position,
                    projectile.Velocity,
                    dt,
                    projectile.RemainingDistance);

                if (TryHit(step.Start, step.End, projectile, actors, tick, out var hit, out bool headshot))
                {
                    if (hit is { } target && target.Has.HasFlag(NetComponents.Health))
                        ApplyDamage(
                            projectile.Shooter,
                            target,
                            actors.FirstOrDefault(actor => actor.Status == target),
                            headshot ? GunConfig.Headshot(projectile.Damage) : projectile.Damage,
                            projectile.Origin,
                            // Where the round was going when it landed, not where it was aimed:
                            // drop has been bending it the whole way, so the two differ at range.
                            projectile.Velocity,
                            tick);
                    SuppressNearImpact(projectile, step.End, actors, tick);
                    projectiles.RemoveAt(i);
                    continue;
                }

                projectile.Position = step.End;
                projectile.Velocity = step.Velocity;
                projectile.RemainingDistance -= step.Distance;
                if (step.Exhausted)
                    projectiles.RemoveAt(i);
            }
        }

        public void ApplyReload(ServerPlayer player, uint tick)
        {
            if (!TryGetActiveWeapon(player, out var weapon)) return;

            var stats = WeaponConfig.Require(weapon.Item.Type);
            if (tick < player.ReloadDoneTick) return;   // already reloading
            if (weapon.Weapon.CurrentAmmo == stats.MagazineCapacity) return;

            // Refill now, block firing until the window passes — observably identical
            // to refilling at the end, with no completion bookkeeping. (The client
            // refills at the end instead so its HUD reads 0 during the reload.)
            weapon.Weapon.CurrentAmmo = stats.MagazineCapacity;
            weapon.Dirty |= NetComponents.Weapon;
            player.ReloadDoneTick = tick + (uint)stats.ReloadTicks;
        }

        private bool TryHit(
            Vector3 start,
            Vector3 end,
            Projectile projectile,
            IEnumerable<ServerPlayer> players,
            uint tick,
            out ServerObject? hit,
            out bool headshot)
        {
            hit = null;
            headshot = false;
            var shooter = projectile.Shooter;
            var segment = end - start;
            float length = segment.Length();
            if (length < 1e-6f) return false;
            var direction = segment / length;

            // Terrain is a distance ceiling. A healthless object still blocks the projectile;
            // it simply produces no damage when selected as the nearest collision.
            float nearestT = TerrainRaycast.Cast(terrain, start, direction, length) is { } ground
                ? ground.Distance
                : float.MaxValue;

            foreach (var obj in objects.All)
            {
                if (!obj.Has.HasFlag(NetComponents.Transform)) continue;
                if (GunMath.HitDistance(start, direction, obj.Transform.Position, length) is not { } t) continue;
                if (t >= nearestT) continue;
                hit = obj;
                headshot = false;   // an object has an origin, not a body
                nearestT = t;
            }

            foreach (var player in players)
            {
                if (player == shooter || player.Status == null) continue;
                var center = player.Position + new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
                float along = Math.Clamp(Vector3.Dot(center - start, direction), 0f, length);
                if (along < nearestT
                    && Vector3.DistanceSquared(start + direction * along, center)
                        <= GunConfig.NearMissRadius * GunConfig.NearMissRadius)
                {
                    player.Spread.Suppress();
                    if (player.Team != shooter.Team
                        && projectile.SuppressedActors.Add(player.Id))
                        suppressions.Enqueue(new AcceptedSuppression(
                            player.Id,
                            shooter.Id,
                            shooter.Team,
                            projectile.Origin,
                            tick));
                }
                if (GunMath.PlayerHitAt(
                        start,
                        direction,
                        player.Position,
                        length,
                        player.State.HasFlag(PlayerStateFlags.Crouching))
                    is not { } actorHit) continue;
                if (actorHit.Distance >= nearestT) continue;

                hit = player.Status;
                headshot = actorHit.Head;
                nearestT = actorHit.Distance;
            }

            return nearestT < float.MaxValue;
        }

        private void ApplyDamage(
            ServerPlayer shooter,
            ServerObject hit,
            ServerPlayer? victim,
            ushort damage,
            Vector3 shotOrigin,
            Vector3 shotVelocity,
            uint tick)
        {
            // Being SHOT is the least ambiguous way to learn you are under fire, and it used to be
            // the one way that told the victim nothing: only near misses raised a suppression, so an
            // NPC hit squarely from four hundred metres took the damage and carried on walking. A
            // hit is a suppression that connected, so it goes down the same path — same
            // MarkUnderFire, same contact, same broadcast to the squad — rather than growing a
            // parallel one. Range never enters into it: the test is the projectile, not a radius.
            if (victim is not null && victim.Team != shooter.Team)
                suppressions.Enqueue(new AcceptedSuppression(
                    victim.Id,
                    shooter.Id,
                    shooter.Team,
                    shotOrigin,
                    tick));

            if (victim is not null) victim.LastDamagedTick = tick;

            bool wasAlive = hit.Health.Current > 0;
            hit.Health.Current = hit.Health.Current > damage
                ? (ushort)(hit.Health.Current - damage)
                : (ushort)0;
            hit.Dirty |= NetComponents.Health;

            Message confirm = Message.Create(MessageSendMode.Unreliable, ServerToClientId.HitConfirm);
            confirm.AddSerializable(new HitConfirmData
            {
                TargetNetworkId = hit.NetworkId,
                Damage = damage,
            });
            if (!shooter.IsMob)
                server.Send(confirm, shooter.Id);
            if (wasAlive && hit.Health.Current == 0)
            {
                // Rides the same dirty bundle as the health that just hit zero, so the client sees
                // the death and what caused it together. Set on the lethal blow only — a corpse is
                // the only thing that reads it.
                hit.Impulse.Velocity = RagdollImpulse.FromBullet(shotVelocity, damage);
                hit.Dirty |= NetComponents.Impulse;

                if (victim is not null) activityFeed?.ReportKill(shooter, victim);
            }
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        internal bool TryGetActiveWeapon(ServerPlayer player, out ServerObject weapon)
        {
            weapon = null!;
            if (player.Hotbar == HotbarSlot.Shovel)
                return false;

            var slot = HotbarConfig.StorageSlot(player.Hotbar);
            // Administrative equips predate the hotbar and remain usable as slot 1.
            if (player.Hotbar == HotbarSlot.Primary && !player.Equipped.ContainsKey(slot))
                slot = EquipSlot.Hand;

            return player.Equipped.TryGetValue(slot, out uint weaponId)
                && objects.TryGet(weaponId, out weapon!)
                && weapon.Has.HasFlag(NetComponents.Weapon)
                && !ItemCatalog.HasBehavior(weapon.Item.Type, ItemBehavior.Grenade);
        }

        /// <summary>
        /// The stored primary regardless of what is currently in the actor's hands. AI planning runs
        /// before each mob selects its slot for the tick, so an assaulter who ended the previous tick
        /// digging must still be recognized as a PPSH carrier while the shovel is selected.
        /// </summary>
        internal bool TryGetPrimaryWeapon(ServerPlayer player, out ServerObject weapon)
        {
            weapon = null!;
            EquipSlot slot = EquipSlot.HotbarPrimary;
            if (!player.Equipped.ContainsKey(slot)) slot = EquipSlot.Hand;
            return player.Equipped.TryGetValue(slot, out uint weaponId)
                && objects.TryGet(weaponId, out weapon!)
                && weapon.Has.HasFlag(NetComponents.Weapon)
                && !ItemCatalog.HasBehavior(weapon.Item.Type, ItemBehavior.Grenade);
        }

    }
}
