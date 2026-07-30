using Riptide;
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

        private readonly Server server;
        private readonly ObjectReplication objects;
        private readonly ChunkMap terrain;
        private readonly ActivityFeedSystem? activityFeed;
        private readonly List<Projectile> projectiles = new();
        private readonly Queue<AcceptedGunshot> gunshots = new();
        private readonly Queue<AcceptedSuppression> suppressions = new();

        public WeaponSystem(
            Server server,
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
            var stats = WeaponConfig.Require(weapon.Item.Type);
            if (tick < player.NextFireTick
                || tick < player.ReloadDoneTick
                || weapon.Weapon.CurrentAmmo <= 0)
                return false;

            player.NextFireTick = tick + (uint)stats.TicksPerShot;
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

            player.Spread.AddRecoil(ballistics);
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

                if (TryHit(step.Start, step.End, projectile, actors, tick, out var hit))
                {
                    if (hit is { } target && target.Has.HasFlag(NetComponents.Health))
                        ApplyDamage(
                            projectile.Shooter,
                            target,
                            actors.FirstOrDefault(actor => actor.Status == target),
                            projectile.Damage);
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
            out ServerObject? hit)
        {
            hit = null;
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
                nearestT = t;
            }

            foreach (var player in players)
            {
                if (player == shooter || player.Status == null) continue;
                var center = player.Position + new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
                float along = Math.Clamp(Vector3.Dot(center - start, direction), 0f, length);
                if (along < nearestT
                    && Vector3.DistanceSquared(start + direction * along, center) <= 4f)
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
                if (GunMath.PlayerHitDistance(start, direction, player.Position, length)
                    is not { } t) continue;
                if (t >= nearestT) continue;

                hit = player.Status;
                nearestT = t;
            }

            return nearestT < float.MaxValue;
        }

        private void ApplyDamage(
            ServerPlayer shooter,
            ServerObject hit,
            ServerPlayer? victim,
            ushort damage)
        {
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
            if (wasAlive && hit.Health.Current == 0 && victim is not null)
                activityFeed?.ReportKill(shooter, victim);
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
                && weapon.Item.Type != ItemType.Grenade;
        }

    }
}
