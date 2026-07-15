using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>Owns every replicated object and all wire traffic about them:
    /// spawn/despawn broadcasts, per-tick dirty-mask deltas, late-joiner catch-up.
    /// Nothing else touches the dictionary — other systems get objects through
    /// Spawn/Despawn/TryGet/All and mark changes via ServerObject.Dirty.</summary>
    public class ObjectReplication
    {
        private readonly Server server;
        private readonly Dictionary<uint, ServerObject> objects = new();
        private uint nextNetworkId = 1; // 0 reserved as no object

        private const NetComponents StreamedComponents = NetComponents.Transform;

        public ObjectReplication(Server server) => this.server = server;

        public IEnumerable<ServerObject> All => objects.Values;

        public bool TryGet(uint networkId, out ServerObject obj) => objects.TryGetValue(networkId, out obj!);


        // init runs BEFORE the spawn broadcast, so the spawn message carries full
        // component state and consumers never see a half-initialized object.
        public ServerObject Spawn(ObjectType type, NetComponents has, Vector3 position, Action<ServerObject>? init = null)
        {
            var obj = new ServerObject { NetworkId = nextNetworkId++, Type = type, Has = has };
            obj.Transform.Position = position;
            init?.Invoke(obj);
            objects[obj.NetworkId] = obj;
            server.SendToAll(CreateSpawnMessage(obj));
            return obj;
        }

        public void Despawn(uint networkId)
        {
            if (!objects.Remove(networkId)) return;
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ObjectDespawn);
            message.AddSerializable(new ObjectDespawnData {NetworkId = networkId});
            server.SendToAll(message);
        }

        /// <summary>Newcomers must learn about existing objects the same way they
        /// learn about existing players. Call from AddPlayer.</summary>
        public void SendCatchUp(ushort clientId)
        {
            foreach (var obj in objects.Values)
                server.Send(CreateSpawnMessage(obj), clientId);
        }

        /// <summary>Call once per tick, after all systems have run.</summary>
        public void BroadcastDirtyStatess(uint tick)
        {
            foreach (var obj in objects.Values)
            {
                if (obj.Dirty == NetComponents.None) continue;

                // Streamed state (transforms): unreliable — the next tick supersedes a loss.
                // Evented state (everything else): reliable — a lost change might never be re-sent.
                var streamed = obj.Dirty & StreamedComponents;
                var evented = obj.Dirty & ~StreamedComponents;

                if (streamed != NetComponents.None)
                    server.SendToAll(CreateStateMessage(obj, streamed, tick, MessageSendMode.Unreliable));
                if (evented != NetComponents.None)
                    server.SendToAll(CreateStateMessage(obj, evented, tick, MessageSendMode.Reliable));

                obj.Dirty = NetComponents.None;
            }
        }

        private Message CreateSpawnMessage(ServerObject obj)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ObjectSpawn);
            message.AddSerializable(new ObjectSpawnData
            {
                NetworkId = obj.NetworkId,
                Type = obj.Type,
                State = Bundle(obj, obj.Has),
            });
            return message;
        }

        private Message CreateSpawnMessage(ServerObject obj, NetComponents mask, uint tick, MessageSendMode mode)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ObjectSpawn);
            message.AddSerializable(new ObjectStateData
            {
                NetworkId = obj.NetworkId,
                Tick = tick,
                State = Bundle(obj, mask),
            });
            return message;
        }

        private Message CreateStateMessage(ServerObject obj, NetComponents mask, uint tick, MessageSendMode mode)
        {
            Message message = Message.Create(mode, ServerToClientId.ObjectState);
            message.AddSerializable(new ObjectStateData
            {
                NetworkId = obj.NetworkId,
                Tick = tick,
                State = Bundle(obj, mask),
            });
            return message;
        }

        // THE one place a ComponentBundle is built server-side. New component =
        // one new line here (plus the checklist in NEXT_STEPS Part 5).
        private static ComponentBundle Bundle(ServerObject obj, NetComponents mask) => new()
        {
            Mask = mask, 
            Transform = obj.Transform,
            Health = obj.Health,
            Weapon = obj.Weapon,
            Owner = obj.Owner,
            Armor = obj.Armor
        };

    }
}