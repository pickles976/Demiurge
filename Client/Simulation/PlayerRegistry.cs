using System.Diagnostics;
using System.Numerics;
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


    /// <summary>
    /// Furthest an actor can plausibly have WALKED between two received snapshots. Sprinting covers
    /// 6 m/s and packets are unreliable, so this allows for a full second of loss and then some;
    /// anything past it was not walked, and interpolating across it drags the model over the map.
    /// </summary>
    private const float MaxWalkedStep = 12f;

    // Shared interpolation clock. 
    private uint newestTick;
    private long newestArrival;

    public double RenderTick => newestTick
        + ((Stopwatch.GetTimestamp() - newestArrival) / (double)Stopwatch.Frequency) * NetworkConfig.TickRate // fraction
        - NetworkConfig.InterpolationDelayTicks;

    public double EstimatedServerTick => newestTick
        + ((Stopwatch.GetTimestamp() - newestArrival) / (double)Stopwatch.Frequency)
        * NetworkConfig.TickRate;

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
        network.ScoreboardReceived += OnScoreboard;
    }

    private void OnPlayerSpawned(PlayerSpawnData data)
    {
        // An actor we already have is not a second actor. Replacing it would leave the VIEW behind:
        // PlayerJoined fires again, PlayerViewFactory adds another Player_{id} entity, and nothing
        // removes the first — which then stands where it spawned forever, collecting the owner's
        // weapons, because items find their owner by entity name and take the oldest match.
        //
        // ObjectRegistry has always ignored a duplicate spawn for exactly this reason, and the
        // asymmetry is what made one doubled stream visible as doubled BODIES and invisible for
        // objects. The stream that caused it is fixed in InProcessNetwork; this is the layer that
        // should not have been able to turn it into orphans in the first place.
        if (players.ContainsKey(data.PlayerId))
        {
            Console.WriteLine($"[Actors] ignoring a repeat spawn for actor {data.PlayerId}");
            return;
        }

        Player player = data.PlayerId == network.ClientId
            ? LocalPlayer = new LocalPlayer(network, terrain, mount)
                { Id = data.PlayerId, Position = data.Position, Team = data.Team }
            : new RemotePlayer {Id = data.PlayerId, Position = data.Position, Team = data.Team};

        players[data.PlayerId] = player;
        PlayerJoined?.Invoke(player);
    }

    /// <summary>
    /// Applies whose side everybody is on, from the roster the server keeps.
    ///
    /// Sides reach the client here rather than through PlayerSpawn, and the reason is one layer up:
    /// a repeat spawn for an actor we already have is deliberately ignored, because replacing a live
    /// actor orphans its view. That guard is right and a re-sent spawn would silently swallow a side
    /// change, so the roster — which is a whole snapshot and therefore idempotent by construction —
    /// carries it instead. A row for somebody we have not been told about yet is simply skipped; his
    /// own spawn message carries his side, and the next board repairs anything that crossed.
    /// </summary>
    private void OnScoreboard(ScoreboardData data)
    {
        foreach (var entry in data.Entries ?? [])
            if (players.TryGetValue(entry.ActorId, out var player))
                player.Team = entry.Team;
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
        network.ScoreboardReceived -= OnScoreboard;
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
                local.RespawnTick = data.RespawnTick;
                // His own input flags are his to author and are rebuilt every frame from the
                // keyboard; the server's are not derivable from input at all and have to be taken
                // from the wire, or they are overwritten the frame after they arrive. That is
                // exactly what left a gunner's camera in first person while the server had him on
                // a mortar.
                local.State = ServerAuthoredState.Merge(local.State, data.State);
                local.Reconcile(data.Move, data.LastProcessedSequence);
                break;
            case RemotePlayer remote:
                // A respawn MOVES the actor rather than walking it there, and the snapshot buffer
                // has no concept of that: it lerps between whatever two samples straddle the render
                // tick, so the corpse's last position and the spawn point become one 100 ms glide
                // across the whole map. Visibility makes it worse rather than hiding it — IsDead
                // reads RespawnTick straight off this packet while the position it is drawn at is
                // still InterpolationDelayTicks in the past, so the model switches back on at the
                // corpse and only then flies to spawn.
                //
                // Dropping the history is what turns that back into a teleport: with only the new
                // sample to work from, the first frame the model is visible again is already at the
                // spawn point. Detected from RespawnTick falling to zero rather than from a
                // distance threshold, because the server tells us exactly when it happened and a
                // threshold would be one more number to get wrong.
                // Two things move an actor without walking it there: a respawn, which announces
                // itself, and a stuck NPC being picked up and relocated, which does not. So the
                // announcement is used where there is one and the SIZE of the step everywhere else
                // — no walk covers this much ground between two snapshots, however many were lost.
                bool respawned = remote.RespawnTick != 0 && data.RespawnTick == 0;
                bool teleported = Vector3.DistanceSquared(remote.Position, data.Position)
                    > MaxWalkedStep * MaxWalkedStep;
                if (respawned || teleported)
                    remote.Snapshots.Clear();

                remote.Snapshots.Store(data.Tick, data.Position);
                remote.Position = data.Position;
                remote.Yaw = data.Yaw;
                remote.Pitch = data.Pitch;
                remote.Hotbar = data.Hotbar;
                remote.State = data.State;
                remote.Velocity = data.Velocity;
                // Already on the wire and simply not read across until footsteps needed it. Without
                // it a remote player is permanently airborne as far as the client is concerned.
                remote.Grounded = data.Grounded;
                remote.RespawnTick = data.RespawnTick;
                break;
        }

    }
}
