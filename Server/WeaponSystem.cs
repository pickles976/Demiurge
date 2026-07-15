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
        private readonly Server server;
        private readonly ObjectReplication objects;

        public WeaponSystem(Server server, ObjectReplication objects)
        {
            this.server = server;
            this.objects = objects;
        }

        public void ApplyFire(ServerPlayer player, PlayerFireData fire, uint tick, IEnumerable<ServerPlayer> players)
        {
            if (!IsFinite(fire.Origin) || !IsFinite(fire.Direction) || !float.IsFinite(fire.RenderTick)) return;

            // Reject views from the future or older than max history
            if (fire.RenderTick > tick || fire.RenderTick < (double)tick - NetworkConfig.MaxRewindTicks) return;
            if (fire.Direction == Vector3.Zero) return;

            // Unarmed players can't fire. The equipped Hand item is the source
            // of truth for ammo — IF it's a gun (Weapon bit); a future non-gun
            // hand item simply can't fire.
            if (!player.Equipped.TryGetValue(EquipSlot.Hand, out uint weaponId)
                || !objects.TryGet(weaponId, out var weapon)
                || !weapon.Has.HasFlag(NetComponents.Weapon)) return;
            var stats = WeaponConfig.Require(weapon.Item.Type);

            // Enforce the same ItemConfig numbers the client predicted with.
            if (tick < player.NextFireTick) return;    // faster than the gun can cycle
            if (tick < player.ReloadDoneTick) return;  // mid-reload
            if (weapon.Weapon.CurrentAmmo <= 0) return;

            // The client supplies the aim, but the shot must leave from roughly where
            // the server has the player. 2m tolerance covers prediction drift.
            if (Vector3.DistanceSquared(fire.Origin, player.Position) > 2f * 2f) return;

            player.NextFireTick = tick + (uint)stats.TicksPerShot;
            weapon.Weapon.CurrentAmmo--;
            weapon.Dirty |= NetComponents.Weapon;       // ammo replicates like any component

            var direction = Vector3.Normalize(fire.Direction);

            // A healthless object (a pickup) still blocks the shot; it just takes no damage.
            if (Raycast(fire.Origin, direction, stats.MaxRange, player, players, fire.RenderTick) is { } hit
                && hit.Has.HasFlag(NetComponents.Health))
            {
                hit.Health.Current = hit.Health.Current > stats.Damage
                    ? (ushort)(hit.Health.Current - stats.Damage)
                    : (ushort)0;
                hit.Dirty |= NetComponents.Health;   // the object pipeline replicates the rest

                // Tell the shooter it landed. Unreliable + shooter-only: cosmetic feedback,
                // the victim's replicated Health remains the truth.
                Message confirm = Message.Create(MessageSendMode.Unreliable, ServerToClientId.HitConfirm);
                confirm.AddSerializable(new HitConfirmData { TargetNetworkId = hit.NetworkId, Damage = stats.Damage });
                server.Send(confirm, player.Id);
            }

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

        public void ApplyReload(ServerPlayer player, uint tick)
        {
            if (!player.Equipped.TryGetValue(EquipSlot.Hand, out uint weaponId)
                || !objects.TryGet(weaponId, out var weapon)
                || !weapon.Has.HasFlag(NetComponents.Weapon)) return;

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

        private ServerObject? Raycast(Vector3 origin, Vector3 direction, float maxRange, ServerPlayer shooter, IEnumerable<ServerPlayer> players, double renderTick)
        {
            ServerObject? nearest = null;
            float nearestT = float.MaxValue;

            foreach (var obj in objects.All)
            {
                // No Transform component = not in the world (equipped weapons keep a
                // stale spawn position) — never hittable.
                if (!obj.Has.HasFlag(NetComponents.Transform)) continue;
                if (GunMath.HitDistance(origin, direction, obj.Transform.Position, maxRange) is not { } t) continue;
                if (t >= nearestT) continue;
                nearest = obj;
                nearestT = t;
            }

            foreach (var player in players)
            {
                if (player == shooter || player.Status == null) continue;

                // It's rewind time
                var seen = player.History.GetInterpolated(renderTick, player.Position);
                var center = seen + new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);

                if (GunMath.HitDistance(origin, direction, center, maxRange) is not {} t) continue;
                if (t >= nearestT) continue;

                nearest = player.Status;
                nearestT = t;
            }

            return nearest;
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    }
}