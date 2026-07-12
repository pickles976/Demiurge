using Demiurge;
  using Demiurge.GameClient;

  public class ObjectRegistry
  {
      private readonly Dictionary<uint, NetObject> objects = new();

      // Updates that arrived before their spawn (reliable spawn racing unreliable/
      // reliable state). Applied in arrival order once the spawn lands.
      private readonly Dictionary<uint, Queue<ObjectStateData>> pendingUpdates = new();
      private const int MaxPendingPerObject = 30;

      public event Action<NetObject>? ObjectSpawned;   // sim -> view boundary
      public event Action<NetObject>? ObjectDespawned;

      public ObjectRegistry(NetworkManager network)
      {
          network.ObjectSpawned += OnSpawn;
          network.ObjectDespawned += OnDespawn;
          network.ObjectStateReceived += OnState;
      }

      private void OnSpawn(ObjectSpawnData data)
      {
          if (objects.ContainsKey(data.NetworkId)) return;

          var obj = new NetObject { NetworkId = data.NetworkId, Type = data.Type, Has = data.State.Mask };
          obj.Transform = data.State.Transform;
          obj.Health = data.State.Health;
          objects[data.NetworkId] = obj;

          if (pendingUpdates.Remove(data.NetworkId, out var queued))
              foreach (var update in queued)
                  Apply(obj, update);

          ObjectSpawned?.Invoke(obj);   // after pending applies, so the view builds from newest state
      }

      private void OnDespawn(ObjectDespawnData data)
      {
          pendingUpdates.Remove(data.NetworkId);
          if (!objects.Remove(data.NetworkId, out var obj)) return;
          ObjectDespawned?.Invoke(obj);
      }

      private void OnState(ObjectStateData data)
      {
          if (objects.TryGetValue(data.NetworkId, out var obj))
          {
              Apply(obj, data);
              return;
          }

          // Spawn hasn't arrived yet: hold the update instead of discarding it.
          if (!pendingUpdates.TryGetValue(data.NetworkId, out var queue))
              pendingUpdates[data.NetworkId] = queue = new Queue<ObjectStateData>();
          if (queue.Count >= MaxPendingPerObject) queue.Dequeue();
          queue.Enqueue(data);
      }

      private static void Apply(NetObject obj, ObjectStateData data)
      {
          if (data.State.Mask.HasFlag(NetComponents.Transform))
          {
              obj.Transform = data.State.Transform;
              obj.Snapshots.Store(data.Tick, data.State.Transform.Position);
          }
          if (data.State.Mask.HasFlag(NetComponents.Health))
              obj.Health = data.State.Health;
      }
  }