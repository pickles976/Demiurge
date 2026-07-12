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

    /// <summary>Read-only view of the live objects, for view-layer queries
    /// (tracer hit tests). Netcode writes, view reads — same contract as ever.</summary>
    public IEnumerable<NetObject> Objects => objects.Values;

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
        CopyComponents(obj, data.State, tick: 0);   // tick 0: spawn state predates any update
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
        => CopyComponents(obj, data.State, data.Tick);


    // THE one place bundle components land on a NetObject — spawn and update both.
    // New component = one new line here.
    private static void CopyComponents(NetObject obj, in ComponentBundle state, uint tick)
    {
        if (state.Mask.HasFlag(NetComponents.Transform))
        {
            obj.Transform = state.Transform;
            obj.Snapshots.Store(tick, state.Transform.Position);
        }
        if (state.Mask.HasFlag(NetComponents.Health)) obj.Health = state.Health;
        if (state.Mask.HasFlag(NetComponents.Weapon)) obj.Weapon = state.Weapon;
        if (state.Mask.HasFlag(NetComponents.Owner)) obj.Owner = state.Owner;
    }
}