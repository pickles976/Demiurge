using System.Diagnostics.Tracing;
using Demiurge;
using Demiurge.GameClient;

public class PlayerRegistry
{
    private readonly Dictionary<ushort, Player> players = new();
    private readonly NetworkManager network;

    public LocalPlayer? LocalPlayer {get; private set;}
    public event Action<Player>? PlayerJoined; // sim -> view boundary
    public event Action<Player>? PlayerLeft;

    // Add listeners
    public PlayerRegistry(NetworkManager network)
    {
        this.network = network;
        network.PlayerSpawned += OnPlayerSpawned;
        network.PlayerDespawned += OnPlayerDespawned;
        network.PlayerPositionReceived += OnPlayerPosition;
    }

    private void OnPlayerSpawned(PlayerSpawnData data)
    {
        Player player = data.PlayerId == network.ClientId
            ? LocalPlayer = new LocalPlayer(network) { Id = data.PlayerId, Position = data.Position }
            : new RemotePlayer {Id = data.PlayerId, Position = data.Position};

        players[data.PlayerId] = player;
        PlayerJoined?.Invoke(player);
    }

    private void OnPlayerDespawned(PlayerDespawnData data)
    {
        Player player = players[data.PlayerId];
        players.Remove(data.PlayerId);
        PlayerLeft?.Invoke(player);
    }

    private void OnPlayerPosition(PlayerPositionData data)
    {

        if (!players.TryGetValue(data.PlayerId, out var player)) return;

        switch (player)
        {
            case LocalPlayer local:
                local.Reconcile(data.Position, data.LastProcessedSequence);
                break;
            case RemotePlayer remote:
                remote.StoreSnapshot(data.Tick, data.Position);
                remote.Position = data.Position;
                remote.Yaw = data.Yaw;
                remote.State = data.State;
                break;
        }

    }
}