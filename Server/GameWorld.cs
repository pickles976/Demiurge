using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    internal class GameWorld
    {
        private readonly Dictionary<ushort, ServerPlayer> players = new();
        private readonly Dictionary<uint, ServerObject> objects = new();
        private uint nextNetworkId = 1; // 0 reserved as noobject 
        private const NetComponents StreamedComponents = NetComponents.Transform;

        // TEMP OBJECT
        private ServerObject? testCrate, testDummy;


        private readonly Server server;

        private uint _Tick = 0;

        private const int MaxQueuedMoves = 3;

        public GameWorld(Server server)
        {
            this.server = server;

            // TEMPORARY demo world content. SendToAll to zero clients is a no-op;
            // connecting clients get these via the AddPlayer catch-up.
            testCrate = SpawnObject(ObjectType.Crate, NetComponents.Transform, new Vector3(3f, 0.5f, 0f));
            testDummy = SpawnObject(ObjectType.TrainingDummy, NetComponents.Transform | NetComponents.Health, new Vector3(-2f, 0f, 2f));
            testDummy.Health = new HealthState { Current = 100, Max = 100 };
            testDummy.Dirty |= NetComponents.Health;
        }

        public ServerObject SpawnObject(ObjectType type, NetComponents has, Vector3 position)
        {
            var obj = new ServerObject { NetworkId = nextNetworkId++, Type = type, Has = has};
            obj.Transform.Position = position;
            objects[obj.NetworkId] = obj;
            server.SendToAll(CreateObjectSpawnMessage(obj));
            return obj;
        }

        public void DespawnObject(uint networkId)
        {
            if (!objects.Remove(networkId)) return;
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ObjectDespawn);
            message.AddSerializable(new ObjectDespawnData { NetworkId = networkId });
            server.SendToAll(message);
        }

        private Message CreateObjectSpawnMessage(ServerObject obj)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.ObjectSpawn);
            message.AddSerializable(new ObjectSpawnData {
                NetworkId = obj.NetworkId,
                Type = obj.Type,
                State = new ComponentBundle { Mask = obj.Has, Transform = obj.Transform, Health = obj.Health }
            });
            return message;
        }

        public void AddPlayer(ushort clientId)
        {
            foreach (var other in players.Values)              // catch the newcomer up
                server.Send(CreateSpawnMessage(other), clientId);
            
            foreach (var obj in objects.Values)                // catch the newcomer up on objects
                server.Send(CreateObjectSpawnMessage(obj), clientId);

            var player = new ServerPlayer { Id = clientId };
            players[clientId] = player;
            server.SendToAll(CreateSpawnMessage(player));      // announce the newcomer
        }

        public void RemovePlayer(ushort clientId) {
            players.Remove(clientId);
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerDespawn);
            message.AddSerializable(
                new PlayerDespawnData { 
                    PlayerId = clientId, 
                    Tick=_Tick});
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

                for (int i  = 0; i < toProcess && player.PendingMoves.TryDequeue(out var move); i++)
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
                
            }

            // TEMPORARY demo behaviors: orbiting crate (streamed state), decaying dummy (evented state).
            if (testCrate != null)
            {
                float t = _Tick / (float)NetworkConfig.TickRate;
                testCrate.Transform.Position = new Vector3(3f * MathF.Cos(t), 0.5f, 3f * MathF.Sin(t));
                testCrate.Dirty |= NetComponents.Transform;
            }
            if (testDummy != null && _Tick % (NetworkConfig.TickRate * 3) == 0)
            {
                testDummy.Health.Current = testDummy.Health.Current > 10
                    ? (ushort)(testDummy.Health.Current - 10)
                    : testDummy.Health.Max;
                testDummy.Dirty |= NetComponents.Health;
            }

            BroadcastObjectStates();
            BroadcastPositions();
        }

        private void BroadcastObjectStates()
        {
            foreach (var obj in objects.Values)
            {
                if (obj.Dirty == NetComponents.None) continue;

                // Streamed state (transforms): unreliable — the next tick supersedes a loss.
                // Evented state (health): reliable — a lost change might never be re-sent.
                var streamed = obj.Dirty & StreamedComponents;
                var evented  = obj.Dirty & ~StreamedComponents;

                if (streamed != NetComponents.None)
                    server.SendToAll(CreateStateMessage(obj, streamed, MessageSendMode.Unreliable));
                if (evented != NetComponents.None)
                    server.SendToAll(CreateStateMessage(obj, evented, MessageSendMode.Reliable));

                obj.Dirty = NetComponents.None;
            }
        }

        private Message CreateStateMessage(ServerObject obj, NetComponents mask, MessageSendMode mode)
        {
            Message message = Message.Create(mode, ServerToClientId.ObjectState);
            message.AddSerializable(new ObjectStateData {
                NetworkId = obj.NetworkId,
                Tick = _Tick,
                State = new ComponentBundle { Mask = mask, Transform = obj.Transform, Health = obj.Health }
            });
            return message;
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
                    new PlayerPositionData { 
                        PlayerId = player.Id, 
                        Tick=_Tick,
                        Position = player.Position, 
                        Yaw = player.Yaw, 
                        State = player.State,
                        LastProcessedSequence = player.LastProcessedSequence });
                server.SendToAll(message); 
            }
        }
    }
}
