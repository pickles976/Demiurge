using System.Collections.Concurrent;
using Demiurge;
using Demiurge.GameClient;

public class ObjectRegistry : IDisposable
{
    private readonly NetworkManager network;
    private readonly Dictionary<uint, NetObject> objects = new();

    // Updates that arrived before their spawn (reliable spawn racing unreliable/
    // reliable state). Applied in arrival order once the spawn lands.
    private readonly Dictionary<uint, Queue<ObjectStateData>> pendingUpdates = new();
    private const int MaxPendingPerObject = 30;

    /// <summary>
    /// The sim -> view boundary, which is also a THREAD boundary — see PlayerRegistry for the whole
    /// argument. Raised from <see cref="Pump"/> on the main thread, never where they are produced.
    /// </summary>
    public event Action<NetObject>? ObjectSpawned;
    public event Action<NetObject>? ObjectDespawned;

    private readonly ConcurrentQueue<(int Kind, NetObject Object)> pendingViewEvents = new();

    /// <summary>Replays everything the network reported since the last frame. MAIN THREAD ONLY.</summary>
    public void Pump()
    {
        while (pendingViewEvents.TryDequeue(out var change))
        {
            switch (change.Kind)
            {
                case 0: ObjectSpawned?.Invoke(change.Object); break;
                case 1: ObjectDespawned?.Invoke(change.Object); break;
                default: HealthDepleted?.Invoke(change.Object); break;
            }
        }
    }
    /// <summary>
    /// Raised at the exact state-update boundary when a living object's health reaches zero.
    /// Consumers cannot reliably poll for this: the server follows death with a respawn health
    /// update, and both may be drained between rendered frames.
    /// </summary>
    public event Action<NetObject>? HealthDepleted;

    /// <summary>Read-only view of the live objects, for view-layer queries
    /// (tracer hit tests). Netcode writes, view reads — same contract as ever.</summary>
    public IEnumerable<NetObject> Objects => objects.Values;

    public bool TryGet(uint networkId, out NetObject obj) => objects.TryGetValue(networkId, out obj!);

    public ObjectRegistry(NetworkManager network)
    {
        this.network = network;
        network.ObjectSpawned += OnSpawn;
        network.ObjectDespawned += OnDespawn;
        network.ObjectStateReceived += OnState;
    }

    public void Dispose()
    {
        network.ObjectSpawned -= OnSpawn;
        network.ObjectDespawned -= OnDespawn;
        network.ObjectStateReceived -= OnState;
        objects.Clear();
        pendingUpdates.Clear();
    }

    private void OnSpawn(ObjectSpawnData data)
    {
        if (objects.ContainsKey(data.NetworkId)) return;

        var obj = new NetObject { NetworkId = data.NetworkId, Type = data.Type, Has = data.State.Mask };
        CopyComponents(obj, data.State, tick: 0, notifyHealthDepleted: false);
        objects[data.NetworkId] = obj;

        if (pendingUpdates.Remove(data.NetworkId, out var queued))
            foreach (var update in queued)
                Apply(obj, update);

        pendingViewEvents.Enqueue((0, obj));   // after pending applies, so the view builds from newest state
    }

    private void OnDespawn(ObjectDespawnData data)
    {
        pendingUpdates.Remove(data.NetworkId);
        if (!objects.Remove(data.NetworkId, out var obj)) return;

        // Where it ENDED, which for anything that detonates is not where it was last seen — see
        // ObjectDespawnData.Position. Written before the event so a subscriber reading the object's
        // transform gets the final answer rather than the newest broadcast one.
        obj.Transform.Position = data.Position;
        pendingViewEvents.Enqueue((1, obj));
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

    private void Apply(NetObject obj, ObjectStateData data)
        => CopyComponents(obj, data.State, data.Tick, notifyHealthDepleted: true);


    // THE one place bundle components land on a NetObject — spawn and update both.
    // New component = one new line here.
    private void CopyComponents(
        NetObject obj,
        in ComponentBundle state,
        uint tick,
        bool notifyHealthDepleted)
    {
        if (state.Mask.HasFlag(NetComponents.Transform))
        {
            obj.Transform = state.Transform;
            obj.Snapshots.Store(tick, state.Transform.Position);
        }
        // Before Health, and that ordering is load-bearing: HealthDepleted fires from inside the
        // branch below, and what it wakes up — the ragdoll — reads the impulse off this object. The
        // WIRE order is still append-only over in ComponentBundle; by the time we are here the whole
        // bundle is already decoded, so the order these land in is ours to choose.
        if (state.Mask.HasFlag(NetComponents.Impulse)) obj.Impulse = state.Impulse;
        if (state.Mask.HasFlag(NetComponents.Supplies)) obj.Supplies = state.Supplies;
        if (state.Mask.HasFlag(NetComponents.Health))
        {
            var previous = obj.Health;
            obj.Health = state.Health;
            if (notifyHealthDepleted && previous.Current > 0 && state.Health.Current == 0)
                pendingViewEvents.Enqueue((2, obj));
        }
        if (state.Mask.HasFlag(NetComponents.Weapon)) obj.Weapon = state.Weapon;
        if (state.Mask.HasFlag(NetComponents.Owner)) obj.Owner = state.Owner;
        if (state.Mask.HasFlag(NetComponents.Armor)) obj.Armor = state.Armor;
        if (state.Mask.HasFlag(NetComponents.Item)) obj.Item = state.Item;
        if (state.Mask.HasFlag(NetComponents.Attachment)) obj.Attachment = state.Attachment;
        if (state.Mask.HasFlag(NetComponents.Team)) obj.Team = state.Team;
    }
}
