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
        private sealed class Projectile
        {
            public required ServerPlayer Shooter { get; init; }
            public required Vector3 Position { get; set; }
            public required Vector3 Velocity { get; set; }
            public required float RemainingDistance { get; set; }
            public required ushort Damage { get; init; }
        }

        private readonly Server server;
        private readonly ObjectReplication objects;
        private readonly ChunkMap terrain;
        private readonly List<Projectile> projectiles = new();

        public WeaponSystem(Server server, ObjectReplication objects, ChunkMap terrain)
        {
            this.server = server;
            this.objects = objects;
            this.terrain = terrain;
        }

        public void ApplyFire(ServerPlayer player, PlayerFireData fire, uint tick)
        {
            if (!IsFinite(fire.Origin) || !IsFinite(fire.Direction) || !float.IsFinite(fire.RenderTick)) return;

            // Reject views from the future or older than max history
            if (fire.RenderTick > tick || fire.RenderTick < (double)tick - NetworkConfig.MaxRewindTicks) return;
            if (fire.Direction.LengthSquared() < 1e-8f) return;

            // Unarmed players can't fire. The equipped Hand item is the source
            // of truth for ammo — IF it's a gun (Weapon bit); a future non-gun
            // hand item simply can't fire.
            if (!TryGetActiveWeapon(player, out var weapon)) return;
            var stats = WeaponConfig.Require(weapon.Item.Type);

            // Enforce the same ItemConfig numbers the client predicted with.
            if (tick < player.NextFireTick) return;    // faster than the gun can cycle
            if (tick < player.ReloadDoneTick) return;  // mid-reload
            if (weapon.Weapon.CurrentAmmo <= 0) return;

            // The client supplies the aim, but the shot must leave from roughly where
            // the server has the player. See GunConfig.MaxFireOriginDistance for what the
            // tolerance has to cover — a barrel swung to full pitch reaches further than it looks.
            if (Vector3.DistanceSquared(fire.Origin, player.Position)
                > GunConfig.MaxFireOriginDistance * GunConfig.MaxFireOriginDistance) return;

            player.NextFireTick = tick + (uint)stats.TicksPerShot;
            weapon.Weapon.CurrentAmmo--;
            weapon.Dirty |= NetComponents.Weapon;       // ammo replicates like any component

            var ballistics = BallisticsConfig.Require(weapon.Item.Type);
            float moa = player.Spread.TotalMoa(player.State, ballistics);
            var direction = Spread.SampleDirection(
                fire.Direction,
                Spread.SigmaRadians(moa),
                Spread.ShotSeed(player.Id, fire.Sequence));

            player.Spread.AddRecoil(ballistics);
            projectiles.Add(new Projectile
            {
                Shooter = player,
                Position = fire.Origin,
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
                Origin = fire.Origin,
                Direction = direction,
            });
            server.SendToAll(fired);
        }

        /// <summary>
        /// Advances shooter dispersion and every live projectile once. Collision is swept over
        /// the whole tick segment, so a fast rifle bullet cannot tunnel through a target between
        /// two 30 Hz updates.
        /// </summary>
        public void Tick(float dt, IEnumerable<ServerPlayer> players)
        {
            foreach (var player in players)
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

                if (TryHit(step.Start, step.End, projectile.Shooter, players, out var hit))
                {
                    if (hit is { } target && target.Has.HasFlag(NetComponents.Health))
                        ApplyDamage(projectile.Shooter, target, projectile.Damage);
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
            ServerPlayer shooter,
            IEnumerable<ServerPlayer> players,
            out ServerObject? hit)
        {
            hit = null;
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
                if (GunMath.HitDistance(start, direction, center, length) is not { } t) continue;
                if (t >= nearestT) continue;

                hit = player.Status;
                nearestT = t;
            }

            return nearestT < float.MaxValue;
        }

        private void ApplyDamage(ServerPlayer shooter, ServerObject hit, ushort damage)
        {
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
            server.Send(confirm, shooter.Id);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private bool TryGetActiveWeapon(ServerPlayer player, out ServerObject weapon)
        {
            weapon = null!;
            EquipSlot slot;
            if (player.IsMob)
            {
                slot = EquipSlot.Hand;
            }
            else if (player.Hotbar == HotbarSlot.Shovel)
                return false;
            else
            {
                slot = HotbarConfig.StorageSlot(player.Hotbar);
                // Administrative equips predate the hotbar and remain usable as slot 1.
                if (player.Hotbar == HotbarSlot.Primary && !player.Equipped.ContainsKey(slot))
                    slot = EquipSlot.Hand;
            }

            return player.Equipped.TryGetValue(slot, out uint weaponId)
                && objects.TryGet(weaponId, out weapon!)
                && weapon.Has.HasFlag(NetComponents.Weapon)
                && weapon.Item.Type != ItemType.Grenade;
        }

    }
}
