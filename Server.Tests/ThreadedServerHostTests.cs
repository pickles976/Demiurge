using Demiurge.GameServer;
using Demiurge.Net;
using Xunit;

namespace Demiurge.ServerTests;

/// <summary>
/// Drives a real <see cref="ServerHost"/> on its own thread over the in-process transport.
/// </summary>
/// <remarks>
/// This is the configuration singleplayer now runs, and it is the one that used to be forbidden: a
/// server and a client stepping concurrently in one process. It only became safe when singleplayer
/// stopped using Riptide, whose <c>Message</c> pool is a process-wide unsynchronised list.
/// <para>
/// Integration-tagged because <c>ChunkTcpServer</c> binds a real listener on
/// <c>ChunkTransport.Port</c>. Do not run these while a game is running, and note that xUnit runs
/// tests within one class sequentially, which is what keeps them from fighting each other for it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class ThreadedServerHostTests
{
    private static ServerOptions OptionsFor(InProcessNetwork network) => new()
    {
        Transport = network.Server,
        AllowCheats = true,
        // An empty map keeps these tests about the threading rather than about world generation.
        RuntimeMap = new RuntimeMap
        {
            MapId = Guid.NewGuid(),
            Name = "threaded-host-test",
            Terrain = new ChunkMap(),
            Placements = [],
        },
    };

    /// <summary>Pumps the client end until <paramref name="until"/> holds, or the deadline passes.</summary>
    private static bool PumpUntil(InProcessNetwork network, Func<bool> until, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            network.Client.Update();
            if (until()) return true;
            Thread.Sleep(2);
        }
        return until();
    }

    [Fact]
    public void ClientReceivesWelcomeFromAServerOnAnotherThread()
    {
        using var network = new InProcessNetwork(seed: 11);
        using var host = new ServerHost(OptionsFor(network));

        bool welcomed = false;
        network.Client.MessageReceived += (_, e) =>
        {
            if ((ServerToClientId)e.MessageId == ServerToClientId.Welcome) welcomed = true;
        };

        host.StartOnOwnThread();
        network.Client.Connect("in-process");

        Assert.True(PumpUntil(network, () => welcomed), "No Welcome arrived from the threaded server.");
    }

    [Fact]
    public void ServerAdvancesTicksWithoutTheClientPumpingIt()
    {
        // The whole point of the change: the client no longer drives the simulation. If this passes
        // only because the client called Update, the thread is not doing its job.
        using var network = new InProcessNetwork(seed: 12);
        using var host = new ServerHost(OptionsFor(network));

        host.StartOnOwnThread();
        network.Client.Connect("in-process");

        int received = 0;
        network.Client.MessageReceived += (_, _) => received++;

        // Sleep, touching nothing. A server pinned to the caller's thread would produce nothing here.
        Thread.Sleep(500);
        network.Client.Update();

        Assert.True(received > 0, "The server produced no traffic while the client was asleep.");
    }

    [Fact]
    public void DisposeStopsTheServerThreadPromptly()
    {
        using var network = new InProcessNetwork(seed: 13);
        var host = new ServerHost(OptionsFor(network));

        host.StartOnOwnThread();
        network.Client.Connect("in-process");
        PumpUntil(network, () => false, timeoutMs: 200);

        DateTime start = DateTime.UtcNow;
        host.Dispose();
        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.True(
            elapsed < TimeSpan.FromSeconds(3),
            $"Dispose took {elapsed.TotalSeconds:F1}s; the join bound is 2s, so the loop is not observing cancellation.");
    }

    [Fact]
    public void ConcurrentTrafficAcrossThreadsIsNeverCorrupted()
    {
        // The regression test for the bug that pinned singleplayer to one thread. Riptide handed two
        // threads the same pooled Message, which surfaced as a truncated read ("N unread bits") on the
        // far end. Our Message pool is [ThreadStatic] and the delivery queues are locked; this hammers
        // both ends at once and checks every payload survived.
        using var network = new InProcessNetwork(seed: 14);
        using var host = new ServerHost(OptionsFor(network));

        host.StartOnOwnThread();
        network.Client.Connect("in-process");

        var corruption = new List<string>();
        network.Client.MessageReceived += (_, e) =>
        {
            // Every server message must decode without over-reading its own payload.
            try
            {
                _ = e.Message.UnreadBytes;
            }
            catch (Exception ex)
            {
                lock (corruption) corruption.Add(ex.Message);
            }
        };

        for (int i = 0; i < 2000; i++)
        {
            var input = new PlayerInputData
            {
                Sequence = (uint)i,
                Intent = new System.Numerics.Vector3(1f, 0f, 0f),
                Yaw = i * 0.01f,
                Pitch = 0f,
            };

            Message message = Message.Create(MessageSendMode.Unreliable, ClientToServerId.PlayerInput);
            message.AddSerializable(input);
            network.Client.Send(message);

            if (i % 50 == 0) network.Client.Update();
        }

        PumpUntil(network, () => false, timeoutMs: 500);

        Assert.Empty(corruption);
    }

    [Fact]
    public void ServerKeepsRunningAcrossManyTicks()
    {
        using var network = new InProcessNetwork(seed: 15);
        using var host = new ServerHost(OptionsFor(network));

        host.StartOnOwnThread();
        network.Client.Connect("in-process");

        int firstWindow = 0;
        network.Client.MessageReceived += (_, _) => firstWindow++;
        PumpUntil(network, () => false, timeoutMs: 400);

        int atCheckpoint = firstWindow;
        PumpUntil(network, () => false, timeoutMs: 400);

        Assert.True(
            firstWindow > atCheckpoint,
            "Server traffic stopped after the first window; the tick loop died or wedged.");
    }
}
