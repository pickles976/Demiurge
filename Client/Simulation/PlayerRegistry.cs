using Demiurge;
using Demiurge.GameClient;

public class PlayerRegistry
{
    private readonly Dictionary<ushort, Player> players = new();
    private readonly NetworkManager network;

    public LocalPlayer? LocalPlayer {get; private set;}
    public event Action<Player>? PlayerJoined; // sim -> view boundary

    // Add listeners
    public PlayerRegistry(NetworkManager network)
    {
        this.network = network;
        network.PlayerSpawned += OnPlayerSpawned;
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

    private void OnPlayerPosition(PlayerPositionData data)
    {
        if (players.TryGetValue(data.PlayerId, out var player)) player.Position = data.Position;
    }
}