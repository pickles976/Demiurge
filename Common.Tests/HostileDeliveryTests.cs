using Demiurge.Net;
using Xunit;

namespace Demiurge.Tests;

/// <summary>
/// Proves the in-process transport actually misbehaves.
/// </summary>
/// <remarks>
/// These are the most important tests in the transport suite, and the reason is worth stating plainly:
/// <b>a fuzz layer that never catches anything is indistinguishable from one that cannot.</b> Every other
/// test here would still pass if the in-process transport quietly delivered everything in order, exactly
/// once — and singleplayer would then be proving nothing about multiplayer while looking green.
/// <para>
/// So this file asserts the misbehaviour is real, and equally that it is <i>bounded</i>: the set of
/// orderings must be a superset of Riptide's and a subset of what a real network can produce. A fake
/// that reorders further than Riptide's deduplication window tolerates invents failures nobody can hit.
/// </para>
/// </remarks>
public class HostileDeliveryTests
{
    private const ushort TestMessageId = 7;

    /// <summary>Sends <paramref name="count"/> numbered messages one way and returns what arrived.</summary>
    private static List<int> RunClientToServer(int seed, int count, MessageSendMode mode)
    {
        using var network = new InProcessNetwork(seed);
        var received = new List<int>();
        network.Server.MessageReceived += (_, e) => received.Add(e.Message.GetInt());

        network.Client.Connect("in-process");
        for (int i = 0; i < count; i++)
        {
            Message message = Message.Create(mode, TestMessageId);
            message.AddInt(i);
            network.Client.Send(message);
        }

        network.Server.Update();
        return received;
    }

    [Fact]
    public void ReliableTrafficIsReordered()
    {
        List<int> received = RunClientToServer(seed: 99, count: 300, mode: MessageSendMode.Reliable);

        Assert.Equal(300, received.Count);
        Assert.NotEqual(Enumerable.Range(0, 300), received);
    }

    [Fact]
    public void ReorderingStaysInsideTheWindow()
    {
        List<int> received = RunClientToServer(seed: 99, count: 300, mode: MessageSendMode.Reliable);

        // position[m] is where message m actually landed.
        var position = new int[received.Count];
        for (int i = 0; i < received.Count; i++) position[received[i]] = i;

        // If m was overtaken by n (sent later, delivered earlier), they must be within the window.
        for (int m = 0; m < position.Length; m++)
        {
            for (int n = m + TransportHostility.ReorderWindow; n < position.Length; n++)
            {
                Assert.True(
                    position[n] > position[m],
                    $"Message {n} was sent {n - m} positions after {m} but overtook it. "
                    + $"That exceeds the {TransportHostility.ReorderWindow}-message window Riptide tolerates, "
                    + "so this is an ordering a real network cannot produce.");
            }
        }
    }

    [Fact]
    public void ReliableTrafficIsNeverDropped()
    {
        // Riptide guarantees reliable delivery. Dropping here would make the fake MORE permissive than
        // the real transport, which inverts the whole "green in singleplayer implies green in
        // multiplayer" guarantee.
        for (int seed = 0; seed < 25; seed++)
        {
            List<int> received = RunClientToServer(seed, count: 200, mode: MessageSendMode.Reliable);
            Assert.Equal(Enumerable.Range(0, 200), received.OrderBy(value => value));
        }
    }

    [Fact]
    public void UnreliableTrafficIsSometimesDropped()
    {
        int totalSent = 0;
        int totalReceived = 0;

        for (int seed = 0; seed < 20; seed++)
        {
            totalSent += 500;
            totalReceived += RunClientToServer(seed, count: 500, mode: MessageSendMode.Unreliable).Count;
        }

        Assert.True(
            totalReceived < totalSent,
            "The unreliable channel never dropped anything across 10,000 messages. "
            + "Singleplayer would then never exercise GameWorld.Tick's starved-input path.");
    }

    [Fact]
    public void UnreliableTrafficIsSometimesDuplicated()
    {
        bool sawDuplicate = false;

        for (int seed = 0; seed < 20 && !sawDuplicate; seed++)
        {
            List<int> received = RunClientToServer(seed, count: 500, mode: MessageSendMode.Unreliable);
            sawDuplicate = received.Count != received.Distinct().Count();
        }

        // Riptide's unreliable channel assigns no sequence id, so nothing dedups it. PlayerInput rides
        // this channel: a duplicate double-applies movement if the input queue applies every arrival.
        Assert.True(
            sawDuplicate,
            "The unreliable channel never duplicated a message. A real UDP path can, and no other test "
            + "we have covers a handler receiving the same input twice.");
    }

    [Fact]
    public void SameSeedProducesTheSameDeliveryDecisions()
    {
        List<int> first = RunClientToServer(seed: 4242, count: 400, mode: MessageSendMode.Unreliable);
        List<int> second = RunClientToServer(seed: 4242, count: 400, mode: MessageSendMode.Unreliable);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DifferentSeedsProduceDifferentDeliveryDecisions()
    {
        List<int> first = RunClientToServer(seed: 1, count: 400, mode: MessageSendMode.Unreliable);
        List<int> second = RunClientToServer(seed: 2, count: 400, mode: MessageSendMode.Unreliable);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DeliveryLogRetainsTheMostRecentDecisions()
    {
        using var network = new InProcessNetwork(seed: 5);
        network.Client.Connect("in-process");

        int sent = TransportHostility.DeliveryLogCapacity * 2;
        for (int i = 0; i < sent; i++)
        {
            Message message = Message.Create(MessageSendMode.Unreliable, TestMessageId);
            message.AddInt(i);
            network.Client.Send(message);
        }

        IReadOnlyList<DeliveryRecord> snapshot = network.Log.Snapshot();

        Assert.Equal(TransportHostility.DeliveryLogCapacity, snapshot.Count);

        // Newest first, and the ring must not have torn: sequence numbers strictly decrease.
        for (int i = 1; i < snapshot.Count; i++)
            Assert.True(snapshot[i].Sequence <= snapshot[i - 1].Sequence);

        Assert.Contains(snapshot, record => record.Verdict == DeliveryVerdict.Dropped);
        Assert.DoesNotContain("(no deliveries recorded)", network.Log.Dump());
    }

    [Fact]
    public void ServerToClientTrafficIsEquallyHostile()
    {
        // The old SimulatedLatencySeconds knob was client-inbound only, so nothing ever mistreated
        // traffic heading to the client. Both directions go through the same queue now.
        using var network = new InProcessNetwork(seed: 77);
        var received = new List<int>();
        network.Client.MessageReceived += (_, e) => received.Add(e.Message.GetInt());
        network.Client.Connect("in-process");

        for (int i = 0; i < 300; i++)
        {
            Message message = Message.Create(MessageSendMode.Unreliable, TestMessageId);
            message.AddInt(i);
            network.Server.SendToAll(message);
        }

        network.Client.Update();

        Assert.NotEmpty(received);
        Assert.True(received.Count < 300 || !received.SequenceEqual(Enumerable.Range(0, 300)));
    }
}

/// <summary>
/// The transport under genuine concurrency, which is the configuration that used to be forbidden.
/// </summary>
/// <remarks>
/// Riptide pools <c>Message</c> in a process-wide list with no lock, so a server and a client
/// allocating at the same time handed two threads the same instance — surfacing as a truncated read
/// ("N unread bits") on the far end and <c>ArgumentOutOfRangeException</c> inside its pool. That is the
/// single fact that pinned singleplayer's server to the client's thread for as long as it was pinned.
/// <para>
/// Our pool is <c>[ThreadStatic]</c> and the delivery queues are locked. This asserts the difference
/// rather than assuming it: both peers run on their own threads, sending and draining at once, and
/// every payload must arrive with its contents intact.
/// </para>
/// </remarks>
public class ConcurrentTransportTests
{
    private const ushort TestMessageId = 9;

    [Fact]
    public void BothPeersMaySendAndDrainConcurrentlyWithoutCorruption()
    {
        using var network = new InProcessNetwork(seed: 31337);
        network.Client.Connect("in-process");

        const int perSide = 20_000;
        var faults = new List<string>();
        int clientSeen = 0;
        int serverSeen = 0;

        // Each message carries a value and its own negation; a torn or shared buffer breaks the pair.
        void Check(int a, int b, string side)
        {
            if (a + b != 0) lock (faults) faults.Add($"{side} got {a}/{b}");
        }

        network.Server.MessageReceived += (_, e) =>
        {
            Check(e.Message.GetInt(), e.Message.GetInt(), "server");
            Interlocked.Increment(ref serverSeen);
        };
        network.Client.MessageReceived += (_, e) =>
        {
            Check(e.Message.GetInt(), e.Message.GetInt(), "client");
            Interlocked.Increment(ref clientSeen);
        };

        var stop = new ManualResetEventSlim();

        var clientThread = new Thread(() =>
        {
            for (int i = 1; i <= perSide; i++)
            {
                Message m = Message.Create(MessageSendMode.Reliable, TestMessageId);
                m.AddInt(i).AddInt(-i);
                network.Client.Send(m);
                if (i % 64 == 0) network.Client.Update();
            }
            while (!stop.IsSet) network.Client.Update();
        });

        var serverThread = new Thread(() =>
        {
            for (int i = 1; i <= perSide; i++)
            {
                Message m = Message.Create(MessageSendMode.Reliable, TestMessageId);
                m.AddInt(i).AddInt(-i);
                network.Server.SendToAll(m);
                if (i % 64 == 0) network.Server.Update();
            }
            while (!stop.IsSet) network.Server.Update();
        });

        clientThread.Start();
        serverThread.Start();
        Thread.Sleep(1500);
        stop.Set();

        Assert.True(clientThread.Join(TimeSpan.FromSeconds(10)), "client thread did not finish");
        Assert.True(serverThread.Join(TimeSpan.FromSeconds(10)), "server thread did not finish");

        Assert.Empty(faults);

        // Reliable never drops, so everything sent must eventually have been seen by the far end.
        Assert.Equal(perSide, serverSeen);
        Assert.Equal(perSide, clientSeen);
    }
}
