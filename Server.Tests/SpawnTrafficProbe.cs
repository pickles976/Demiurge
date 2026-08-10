using System.Numerics;
using Demiurge.GameServer;
using Demiurge.Net;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// One client, one announcement per thing.
///
/// Written to settle a report of duplicated NPC bodies — weapons standing at spawn while the live
/// NPCs walked off empty-handed — because a second spawn for a live id is the one thing the SERVER
/// could do to cause it: the client's registry replaces the sim actor and the view it left behind
/// keeps rendering, and items find their owner by entity name and attach to whichever came first.
/// It does not happen, so that class of cause is ruled out and the search belongs on the client.
///
/// The measurement trap it walks into deliberately: every object exists before the first client
/// connects, so its creation broadcast went to nobody. Count those and all 132 of them look
/// announced twice, which is a false positive that reads exactly like the bug being hunted. Only
/// what is sent AFTER the client joins is evidence about that client.
/// </summary>
/// <remarks>
/// In the real-port collection because constructing a <see cref="GameWorld"/> starts its terrain
/// TCP listener; run in parallel with the other integration tests, it fights them for the port.
/// </remarks>
[Collection(RealPortCollection.Name)]
public sealed class SpawnTrafficProbe(ITestOutputHelper output)
{
    private sealed class RecordingServer : INetServer
    {
        // Required by INetServer; a recorder never raises them. The probe drives the world directly.
#pragma warning disable CS0067
        public event EventHandler<NetMessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<NetClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<NetClientDisconnectedEventArgs>? ClientDisconnected;
#pragma warning restore CS0067

        public readonly List<(ushort Client, ushort ActorId, Vector3 Position)> Spawns = [];
        public readonly List<(ushort Client, ushort ActorId)> Despawns = [];
        public readonly List<(uint NetworkId, ObjectType Type, ComponentBundle State)> ObjectSpawns = [];
        public readonly List<uint> ObjectDespawns = [];

        public void Start(ushort port, int maxClientCount) { }
        public void Stop() { }
        public void Update() { }
        public void Dispose() { }

        public void Send(Message message, ushort clientId) => Record(message, clientId);
        public void SendToAll(Message message) => Record(message, ushort.MaxValue);

        private void Record(Message message, ushort clientId)
        {
            if (message.Id == (ushort)ServerToClientId.PlayerSpawn)
            {
                var spawn = message.GetSerializable<PlayerSpawnData>();
                Spawns.Add((clientId, spawn.PlayerId, spawn.Position));
            }
            else if (message.Id == (ushort)ServerToClientId.PlayerDespawn)
            {
                var despawn = message.GetSerializable<PlayerDespawnData>();
                Despawns.Add((clientId, despawn.PlayerId));
            }
            else if (message.Id == (ushort)ServerToClientId.ObjectSpawn)
            {
                var spawn = message.GetSerializable<ObjectSpawnData>();
                ObjectSpawns.Add((spawn.NetworkId, spawn.Type, spawn.State));
            }
            else if (message.Id == (ushort)ServerToClientId.ObjectDespawn)
            {
                ObjectDespawns.Add(message.GetSerializable<ObjectDespawnData>().NetworkId);
            }
            message.Release();
        }

    }

    [Fact]
    [Trait("Category", "Integration")]
    public void OneClientReceivesOneSpawnPerActor()
    {
        string root = FindRepositoryRoot();
        var map = RuntimeMapSerializer.Load(Path.Combine(root, "maps", "conquest", "runtime.dmap"));

        var server = new RecordingServer();
        var world = new GameWorld(server, map, initialPlayerTeam: 1, initialNpcsPerTeam: 16);
        // Stop() releases the terrain listener's port and the navigation workers. Without it this
        // holds port 7778 for the rest of the test session and every later test that binds it fails
        // — which is how it presented: unrelated Riptide tests failing only when run after this one.
        using var stopping = new WorldStop(world);

        // Everything sent before the client exists is addressed to nobody; only what a joining
        // client is actually told counts.
        int beforeJoin = server.Spawns.Count;
        int objectsBeforeJoin = server.ObjectSpawns.Count;
        world.AddPlayer(clientId: 1);
        int atJoin = server.Spawns.Count - beforeJoin;

        // Long enough for the first respawn waves: a respawn that re-announced its actor would show
        // up here as a second spawn for an id the client already has.
        for (int tick = 0; tick < 30 * 90; tick++)
            world.Tick(NetworkConfig.FixedDt);

        var delivered = server.Spawns
            .Skip(beforeJoin)
            .Where(entry => entry.Client is 1 or ushort.MaxValue)
            .ToList();

        var byActor = delivered
            .GroupBy(entry => entry.ActorId)
            .OrderByDescending(group => group.Count())
            .ToList();

        output.WriteLine($"spawns before the client joined: {beforeJoin} (addressed to nobody)");
        output.WriteLine($"spawns delivered during AddPlayer: {atJoin}");
        output.WriteLine($"distinct actors announced to the client: {byActor.Count}");
        output.WriteLine($"total announcements to the client: {delivered.Count}");
        output.WriteLine($"despawns to the client: {server.Despawns.Count}");

        // ---- items ----------------------------------------------------------------------------
        // What every actor is holding at the end of the run, checked against the objects the client
        // has actually been told about: an NPC whose weapon object is gone is invisible-handed on
        // every client, with no client bug involved at all.
        // Skipping the pre-join broadcasts for the same reason the actor count does: they were
        // addressed to a client that did not exist, and counting them makes every object in the
        // world look announced twice.
        var objectSpawns = server.ObjectSpawns.Skip(objectsBeforeJoin).ToList();
        var despawned = server.ObjectDespawns.ToHashSet();
        var live = objectSpawns
            .Where(entry => !despawned.Contains(entry.NetworkId))
            .GroupBy(entry => entry.NetworkId)
            .ToDictionary(group => group.Key, group => group.Last());

        output.WriteLine(string.Empty);
        output.WriteLine($"object spawns to the client: {objectSpawns.Count}, despawns: {server.ObjectDespawns.Count}");

        var repeated = objectSpawns
            .GroupBy(entry => entry.NetworkId)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key)
            .ToList();
        output.WriteLine($"object ids announced more than once: {repeated.Count}");
        foreach (var group in repeated.Take(12))
            output.WriteLine(
                $"  #{group.Key} x{group.Count()}: "
                + string.Join(" | ", group.Select(entry =>
                    $"{entry.Type}/{entry.State.Item.Type} owner {entry.State.Owner.PlayerId} "
                    + $"slot {entry.State.Attachment.Slot} mask [{entry.State.Mask}]")));

        int armed = 0;
        int unarmed = 0;
        int missingObject = 0;
        foreach (var (id, _) in world.ActorSnapshot())
        {
            if (!world.TryGetActor(id, out var actor)) continue;
            bool hasPrimary = false;
            foreach (var (slot, networkId) in actor.Equipped)
            {
                if (slot is not (EquipSlot.HotbarPrimary or EquipSlot.Hand)) continue;
                hasPrimary = true;
                if (!live.ContainsKey(networkId)) missingObject++;
            }
            if (hasPrimary) armed++;
            else unarmed++;
        }

        output.WriteLine(
            $"actors with a primary slot: {armed}, with none: {unarmed}, "
            + $"whose primary object the client never saw or saw despawned: {missingObject}");

        foreach (var group in byActor.Where(group => group.Count() > 1))
            output.WriteLine(
                $"  actor {group.Key} announced {group.Count()} times at "
                + string.Join(" | ", group.Select(entry => entry.Position)));

        Assert.All(byActor, group => Assert.Single(group));
        Assert.All(repeated, group => Assert.Single(group));

        // The other half of "the NPC has no weapon": it would be true of every client, with no
        // client involved, if the server had stopped tracking the object.
        Assert.Equal(0, missingObject);
        Assert.Equal(0, unarmed);
    }

    private sealed class WorldStop(GameWorld world) : IDisposable
    {
        public void Dispose() => world.Stop();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DemiurgeSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
