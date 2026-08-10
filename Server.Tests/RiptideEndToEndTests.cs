using Demiurge.GameServer;
using Demiurge.Net;
using Xunit;
using Xunit.Abstractions;

namespace Demiurge.ServerTests;

/// <summary>
/// A real client joining a real server over real UDP.
/// </summary>
/// <remarks>
/// The conformance suite proves the two transports satisfy the same contract, and the threaded tests
/// prove the in-process one carries a live server. Neither actually opens a socket and joins, which is
/// the path every remote player takes and the one the transport rewrite touched most: our
/// <see cref="Message"/> now rides inside a Riptide datagram as a length-prefixed blob, and nothing
/// had exercised that end to end.
/// <para>
/// BOTH PEERS ARE PUMPED FROM THIS THREAD, and that is mandatory rather than tidy. Riptide pools
/// <c>Message</c> in an unsynchronised process-wide list, so a Riptide server and a Riptide client
/// allocating concurrently corrupts it. That is exactly why singleplayer moved to the in-process
/// transport, and why this test must NOT use <c>StartOnOwnThread</c>.
/// </para>
/// <para>
/// Integration-tagged because it binds <c>NetworkConfig.Port</c> and <c>ChunkTransport.Port</c> for
/// real. Do not run it while a game is running.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(RealPortCollection.Name)]
public class RiptideEndToEndTests(ITestOutputHelper output)
{
    private static ServerOptions Options() => new()
    {
        // Transport left null on purpose: this is the default path a dedicated server takes.
        AllowCheats = true,
        RuntimeMap = new RuntimeMap
        {
            MapId = Guid.NewGuid(),
            Name = "riptide-end-to-end",
            Terrain = new ChunkMap(),
            Placements = [],
        },
    };

    [Fact]
    public void AClientJoinsOverUdpAndReceivesWelcome()
    {
        using var host = new ServerHost(Options());
        using var client = new RiptideNetClient();

        WelcomeData? welcome = null;
        bool connected = false;

        client.Connected += (_, _) => connected = true;
        client.MessageReceived += (_, e) =>
        {
            if ((ServerToClientId)e.MessageId == ServerToClientId.Welcome)
                welcome = e.Message.GetSerializable<WelcomeData>();
        };

        host.Start();
        client.Connect($"{NetworkConfig.ServerHost}:{NetworkConfig.Port}");

        // One thread drives both peers. See the remark above for why that is not negotiable.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (welcome is null && DateTime.UtcNow < deadline)
        {
            host.Step();
            client.Update();
            Thread.Sleep(1);
        }

        Assert.True(connected, "Client never connected over UDP.");
        Assert.NotNull(welcome);

        // Welcome is the message that proves the whole chain: our Message serialized it, the Riptide
        // envelope carried it as a byte blob, and the far end read it back with the fields intact.
        Assert.NotEqual(0, welcome!.Value.ClientId);
        Assert.NotEqual(Guid.Empty, welcome.Value.ChunkToken);
        Assert.Equal(NetworkConfig.ProtocolVersion, welcome.Value.ProtocolVersion);
        Assert.Equal(ItemCatalog.Registry.GameplayHash, welcome.Value.GameplayHash);

        output.WriteLine($"joined as client {welcome.Value.ClientId}, chunk token {welcome.Value.ChunkToken}");
    }

    [Fact]
    public void ClientInputReachesTheServerOverUdp()
    {
        using var host = new ServerHost(Options());
        using var client = new RiptideNetClient();

        bool welcomed = false;
        int serverMessages = 0;

        client.MessageReceived += (_, e) =>
        {
            if ((ServerToClientId)e.MessageId == ServerToClientId.Welcome) welcomed = true;
            serverMessages++;
        };

        host.Start();
        client.Connect($"{NetworkConfig.ServerHost}:{NetworkConfig.Port}");

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!welcomed && DateTime.UtcNow < deadline)
        {
            host.Step();
            client.Update();
            Thread.Sleep(1);
        }

        Assert.True(welcomed, "Never welcomed; the join path is broken before input matters.");

        // Round-trips the other direction: our serializer, the envelope, real UDP, the server's switch.
        for (int i = 0; i < 60; i++)
        {
            Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.PlayerInput);
            message.AddSerializable(new PlayerInputData
            {
                Sequence = (uint)i,
                Intent = new System.Numerics.Vector3(1f, 0f, 0f),
                Yaw = i * 0.05f,
            });
            client.Send(message);

            host.Step();
            client.Update();
            Thread.Sleep(2);
        }

        // The server replies with position/state traffic once a player exists, so continued inbound
        // messages are the evidence the server accepted the join and is simulating.
        Assert.True(serverMessages > 1, $"Only {serverMessages} messages arrived; the server went quiet.");
        output.WriteLine($"{serverMessages} messages received over UDP");
    }
}
