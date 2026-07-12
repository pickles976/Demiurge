using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    internal class GameWorld
    {


        private readonly ObjectReplication objects;

        private readonly Dictionary<ushort, ServerPlayer> players = new();

        private const float PickupRadiusSq = 0.75f * 0.75f;

        // TEMP OBJECTS
        private ServerObject? testDummy;


        private readonly Server server;

        private uint _Tick = 0;

        private const int MaxQueuedMoves = 3;

        private ServerObject? RaycastObjects(Vector3 origin, Vector3 direction, float maxRange)
        {
            ServerObject? nearest = null;
            float nearestT = float.MaxValue;

            foreach (var obj in objects.All)
            {
                // No Transform component = not in the world (equipped weapons ride
                // their owner's hand and keep a stale spawn position) — never hittable.
                if (!obj.Has.HasFlag(NetComponents.Transform)) continue;

                if (GunMath.HitDistance(origin, direction, obj.Transform.Position, maxRange) is not { } t) continue;
                if (t >= nearestT) continue;

                nearest = obj;
                nearestT = t;
            }
            return nearest;
        }

        public GameWorld(Server server)
        {
            this.server = server;
            objects = new ObjectReplication(server);

            SpawnWeaponPickup(WeaponType.Ak47, new Vector3(3f, 0f, 0f));
            SpawnWeaponPickup(WeaponType.Ak47, new Vector3(-3f, 0f, -3f));
        }

        public ServerObject SpawnWeaponPickup(WeaponType type, Vector3 position)
        {
            return objects.Spawn(ObjectType.WeaponPickup, NetComponents.Transform | NetComponents.Weapon, position,
                obj => obj.Weapon = new WeaponState { Type = type, CurrentAmmo = WeaponConfig.Get(type).MagazineCapacity });
        }

        public void AddPlayer(ushort clientId)
        {
            foreach (var other in players.Values)              // catch the newcomer up
                server.Send(CreateSpawnMessage(other), clientId);

            objects.SendCatchUp(clientId); // catch the newcomer up on objects

            var player = new ServerPlayer { Id = clientId };
            players[clientId] = player;
            server.SendToAll(CreateSpawnMessage(player));      // announce the newcomer
        }

        public void RemovePlayer(ushort clientId)
        {
            if (players.Remove(clientId, out var player) && player.WeaponId != 0)
                objects.Despawn(player.WeaponId);                // weapon leaves with its owner

            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerDespawn);
            message.AddSerializable(
                new PlayerDespawnData
                {
                    PlayerId = clientId,
                    Tick = _Tick
                });
            server.SendToAll(message);
        }

        public void ApplyInput(ushort clientId, PlayerInputData input)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            if (input.Sequence <= player.LastReceivedSequence) return; // dupe or out of order

            if (!IsFinite(input.Intent) || !float.IsFinite(input.Yaw)) return;

            player.LastReceivedSequence = input.Sequence;
            player.PendingMoves.Enqueue(input);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public void Tick(float dt)
        {
            _Tick++;

            foreach (var player in players.Values)
            {

                // If the queue starts overflowing, consume at a faster rate
                int toProcess = player.PendingMoves.Count > MaxQueuedMoves ? 2 : 1;
                bool processedAny = false;

                for (int i = 0; i < toProcess && player.PendingMoves.TryDequeue(out var move); i++)
                {
                    player.Position = PlayerMovement.Step(player.Position, move.Intent, move.State, dt);
                    player.State = move.State;
                    player.Yaw = move.Yaw;
                    player.LastIntent = move.Intent;
                    player.LastProcessedSequence = move.Sequence;
                    processedAny = true;
                }

                // Queue starved, just reuse last player input
                if (!processedAny)
                    player.Position = PlayerMovement.Step(player.Position, player.LastIntent, player.State, dt);

                TryPickupWeapon(player);
            }

            // TEMPORARY demo behavior: the dummy comes back to life a moment after
            // dying, so there is always something to shoot.
            if (testDummy != null && testDummy.Health.Current == 0 && _Tick % (NetworkConfig.TickRate * 3) == 0)
            {
                testDummy.Health.Current = testDummy.Health.Max;
                testDummy.Dirty |= NetComponents.Health;
            }

            objects.BroadcastDirtyStatess(_Tick);
            BroadcastPositions();
        }

        private void TryPickupWeapon(ServerPlayer player)
        {
            if (player.WeaponId != 0) return;   // armed players ignore pickups

            // Find first, act after: DespawnObject/SpawnObject mutate the
            // dictionary and must not run inside its enumeration.
            ServerObject? pickup = null;
            foreach (var obj in objects.All)
            {
                if (obj.Type != ObjectType.WeaponPickup) continue;
                if (Vector3.DistanceSquared(obj.Transform.Position, player.Position) > PickupRadiusSq) continue;
                pickup = obj;
                break;
            }
            if (pickup == null) return;

            var carried = pickup.Weapon;        // ammo carries over from the pickup
            objects.Despawn(pickup.NetworkId);

            var weapon = objects.Spawn(ObjectType.EquippedWeapon, NetComponents.Weapon | NetComponents.Owner, player.Position,
                obj =>
                {
                    obj.Weapon = carried;
                    obj.Owner = new OwnerState { PlayerId = player.Id };
                });
            player.WeaponId = weapon.NetworkId;
        }

        public void ApplyFire(ushort clientId, PlayerFireData fire)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            if (!IsFinite(fire.Origin) || !IsFinite(fire.Direction)) return;
            if (fire.Direction == Vector3.Zero) return;

            // Unarmed players can't fire; the equipped weapon object is the source
            // of truth for ammo, and its type keys the config both ends enforce.
            if (player.WeaponId == 0 || !objects.TryGet(player.WeaponId, out var weapon)) return;
            var stats = WeaponConfig.Get(weapon.Weapon.Type);

            // Enforce the same WeaponConfig numbers the client predicted with.
            if (_Tick < player.NextFireTick) return;    // faster than the gun can cycle
            if (_Tick < player.ReloadDoneTick) return;  // mid-reload
            if (weapon.Weapon.CurrentAmmo <= 0) return;

            // The client supplies the aim, but the shot must leave from roughly where the
            // server has the player. 2m tolerance covers prediction drift; anything past
            // that is a teleported origin.
            if (Vector3.DistanceSquared(fire.Origin, player.Position) > 2f * 2f) return;

            player.NextFireTick = _Tick + (uint)stats.TicksPerShot;
            weapon.Weapon.CurrentAmmo--;
            weapon.Dirty |= NetComponents.Weapon;       // ammo replicates like any component

            var direction = Vector3.Normalize(fire.Direction);

            // A healthless object (a pickup) still blocks the shot; it just takes no damage.
            if (RaycastObjects(fire.Origin, direction, stats.MaxRange) is { } hit
                && hit.Has.HasFlag(NetComponents.Health))
            {
                hit.Health.Current = hit.Health.Current > stats.Damage
                    ? (ushort)(hit.Health.Current - stats.Damage)
                    : (ushort)0;
                hit.Dirty |= NetComponents.Health;   // the object pipeline replicates the rest
            }

            // Cosmetic rebroadcast so other clients can draw the tracer and play the
            // shot sound. Unreliable: a lost tracer is nothing; the damage above is
            // already safely in the object pipeline. Only ACCEPTED shots get here,
            // so rejected fire never flashes on anyone's screen.
            Message fired = Message.Create(MessageSendMode.Unreliable, ServerToClientId.PlayerFired);
            fired.AddSerializable(new PlayerFiredData
            {
                PlayerId = clientId,
                Weapon = weapon.Weapon.Type,
                Origin = fire.Origin,
                Direction = direction,
            });
            server.SendToAll(fired);
        }

        public void ApplyReload(ushort clientId)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            if (player.WeaponId == 0 || !objects.TryGet(player.WeaponId, out var weapon)) return;

            var stats = WeaponConfig.Get(weapon.Weapon.Type);
            if (_Tick < player.ReloadDoneTick) return;   // already reloading
            if (weapon.Weapon.CurrentAmmo == stats.MagazineCapacity) return;

            // Refill now, block firing until the window passes. Through the fire gate this
            // is observably identical to refilling at the end, with no completion
            // bookkeeping. (The client refills at the end instead, so the ammo HUD
            // honestly reads 0 during the reload — the asymmetry is deliberate.)
            weapon.Weapon.CurrentAmmo = stats.MagazineCapacity;
            weapon.Dirty |= NetComponents.Weapon;
            player.ReloadDoneTick = _Tick + (uint)stats.ReloadTicks;
        }


        private Message CreateSpawnMessage(ServerPlayer player)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerSpawn);
            message.AddSerializable(new PlayerSpawnData { PlayerId = player.Id, Position = player.Position });
            return message;
        }

        private void BroadcastPositions()
        {
            foreach (var player in players.Values)
            {
                Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.PlayerPosition);
                message.AddSerializable(
                    new PlayerPositionData
                    {
                        PlayerId = player.Id,
                        Tick = _Tick,
                        Position = player.Position,
                        Yaw = player.Yaw,
                        State = player.State,
                        LastProcessedSequence = player.LastProcessedSequence
                    });
                server.SendToAll(message);
            }
        }
    }
}
