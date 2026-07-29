using System.Diagnostics;
using Demiurge;
using Demiurge.GameClient;

public class PlayerRegistry : IDisposable
{
    private readonly Dictionary<ushort, Player> players = new();
    private readonly NetworkManager network;
    private readonly TerrainState terrain;   // the local player predicts movement against it
    private readonly WeaponMount mount;      // ... and derives its shot origin from it

    public LocalPlayer? LocalPlayer {get; private set;}
    public event Action<Player>? PlayerJoined; // sim -> view boundary
    public event Action<Player>? PlayerLeft;
    public IEnumerable<Player> Players => players.Values;


    // Shared interpolation clock. 
    private uint newestTick;
    private long newestArrival;

    public double RenderTick => newestTick
        + ((Stopwatch.GetTimestamp() - newestArrival) / (double)Stopwatch.Frequency) * NetworkConfig.TickRate // fraction
        - NetworkConfig.InterpolationDelayTicks;

    // Add listeners
    public PlayerRegistry(NetworkManager network, TerrainState terrain, WeaponMount mount)
    {
        this.network = network;
        this.terrain = terrain;
        this.mount = mount;
        newestArrival = Stopwatch.GetTimestamp();
        network.PlayerSpawned += OnPlayerSpawned;
        network.PlayerDespawned += OnPlayerDespawned;
        network.PlayerPositionReceived += OnPlayerPosition;
    }

    private void OnPlayerSpawned(PlayerSpawnData data)
    {
        Player player = data.PlayerId == network.ClientId
            ? LocalPlayer = new LocalPlayer(network, terrain, mount)
                { Id = data.PlayerId, Position = data.Position, Team = data.Team }
            : new RemotePlayer {Id = data.PlayerId, Position = data.Position, Team = data.Team};

        players[data.PlayerId] = player;
        PlayerJoined?.Invoke(player);
    }

    private void OnPlayerDespawned(PlayerDespawnData data)
    {
        Player player = players[data.PlayerId];
        players.Remove(data.PlayerId);
        PlayerLeft?.Invoke(player);
    }

    public bool TryGet(ushort playerId, out Player player) => players.TryGetValue(playerId, out player!);

    public void Dispose()
    {
        network.PlayerSpawned -= OnPlayerSpawned;
        network.PlayerDespawned -= OnPlayerDespawned;
        network.PlayerPositionReceived -= OnPlayerPosition;
        players.Clear();
        LocalPlayer = null;
    }

    private void OnPlayerPosition(PlayerPositionData data)
    {

        if (data.Tick > newestTick)
        {
            newestTick = data.Tick;
            newestArrival = Stopwatch.GetTimestamp();
        }

        if (!players.TryGetValue(data.PlayerId, out var player)) return;

        switch (player)
        {
            case LocalPlayer local:
                local.Reconcile(data.Move, data.LastProcessedSequence);
                break;
            case RemotePlayer remote:
                remote.Snapshots.Store(data.Tick, data.Position);
                remote.Position = data.Position;
                remote.Yaw = data.Yaw;
                remote.Pitch = data.Pitch;
                remote.Hotbar = data.Hotbar;
                remote.State = data.State;
                remote.Velocity = data.Velocity;
                break;
        }

    }
}
