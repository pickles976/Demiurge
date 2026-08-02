using Demiurge.Net;
using Xunit;

namespace Demiurge.Tests;

/// <summary>
/// Layer 2 of the conformance suite: one test body, both transports.
/// </summary>
/// <remarks>
/// Anything asserted here holds for the Riptide-backed transport and the in-process one by
/// construction, because the derived classes below supply nothing but a factory. The in-process leg runs
/// in the fast suite; the Riptide leg binds real loopback sockets and is tagged Integration.
/// </remarks>
public abstract class TransportConformance
{
    protected abstract TransportFixture Create();

    protected abstract class TransportFixture : IDisposable
    {
        public abstract INetServer Server { get; }
        public abstract INetClient Client { get; }
        public abstract void Connect();

        /// <summary>Move traffic until both ends are quiescent.</summary>
        public abstract void Pump();

        public abstract void Dispose();
    }

    private const ushort TestMessageId = 42;

    private static Message Numbered(MessageSendMode mode, int value)
    {
        Message message = Message.Create(mode, TestMessageId);
        message.AddInt(value);
        return message;
    }

    [Fact]
    public void ConnectingRaisesClientConnectedOnTheServer()
    {
        using TransportFixture fixture = Create();

        ushort connectedId = 0;
        fixture.Server.ClientConnected += (_, e) => connectedId = e.ClientId;

        fixture.Connect();
        fixture.Pump();

        Assert.NotEqual(0, connectedId);
    }

    [Fact]
    public void EveryReliableMessageArrivesExactlyOnce()
    {
        using TransportFixture fixture = Create();
        fixture.Connect();
        fixture.Pump();

        var received = new List<int>();
        fixture.Server.MessageReceived += (_, e) => received.Add(e.Message.GetInt());

        const int count = 50;
        for (int i = 0; i < count; i++)
            fixture.Client.Send(Numbered(MessageSendMode.Reliable, i));

        fixture.Pump();

        Assert.Equal(count, received.Count);
        Assert.Equal(Enumerable.Range(0, count), received.OrderBy(value => value));
    }

    [Fact]
    public void PayloadArrivesByteIdentical()
    {
        using TransportFixture fixture = Create();
        fixture.Connect();
        fixture.Pump();

        byte[]? arrived = null;
        fixture.Server.MessageReceived += (_, e) => arrived = e.Message.GetBytes();

        byte[] sent = Enumerable.Range(0, 200).Select(value => (byte)(value % 251)).ToArray();
        Message message = Message.Create(MessageSendMode.Reliable, TestMessageId);
        message.AddBytes(sent);
        fixture.Client.Send(message);

        fixture.Pump();

        Assert.Equal(sent, arrived);
    }

    [Fact]
    public void OversizedMessageThrowsWhereItIsBuilt()
    {
        using TransportFixture fixture = Create();

        Message message = Message.Create(MessageSendMode.Reliable, TestMessageId);
        var exception = Assert.Throws<MessageTooLargeException>(
            () => message.AddBytes(new byte[Message.MaxPayloadBytes + 1]));

        Assert.Equal(TestMessageId, exception.MessageId);
    }
}

/// <summary>The in-process transport. Fast suite.</summary>
public class InProcessTransportConformance : TransportConformance
{
    protected override TransportFixture Create() => new Fixture(seed: 1234);

    private sealed class Fixture : TransportFixture
    {
        private readonly InProcessNetwork network;

        public Fixture(int seed) => network = new InProcessNetwork(seed);

        public override INetServer Server => network.Server;
        public override INetClient Client => network.Client;
        public override void Connect() => network.Client.Connect("in-process");

        public override void Pump()
        {
            // Twice, so a message sent by a handler during the first pass is delivered by the second.
            for (int i = 0; i < 2; i++)
            {
                network.Server.Update();
                network.Client.Update();
            }
        }

        public override void Dispose() => network.Dispose();
    }
}

/// <summary>
/// The real Riptide transport over loopback sockets.
/// </summary>
/// <remarks>
/// Both peers live in this one process and are pumped from this one thread, which is mandatory rather
/// than convenient: Riptide pools <c>Message</c> in an unsynchronised process-wide list, so a server and
/// a client allocating concurrently corrupts it. That constraint is exactly why singleplayer moved to
/// the in-process transport instead of this one.
/// </remarks>
[Trait("Category", "Integration")]
public class RiptideTransportConformance : TransportConformance
{
    protected override TransportFixture Create() => new Fixture();

    private sealed class Fixture : TransportFixture
    {
        // Away from NetworkConfig.Port so a running game does not collide with the suite.
        private const ushort TestPort = 7791;

        private readonly RiptideNetServer server = new();
        private readonly RiptideNetClient client = new();
        private bool connected;

        public Fixture()
        {
            server.Start(TestPort, maxClientCount: 4);
            client.Connected += (_, _) => connected = true;
        }

        public override INetServer Server => server;
        public override INetClient Client => client;

        public override void Connect()
        {
            client.Connect($"127.0.0.1:{TestPort}");

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!connected && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(1);
            }

            Assert.True(connected, "Riptide client did not connect over loopback within 5s.");
        }

        public override void Pump()
        {
            // Real sockets need wall-clock time; a single pump would race the OS.
            for (int i = 0; i < 100; i++)
            {
                server.Update();
                client.Update();
                Thread.Sleep(1);
            }
        }

        public override void Dispose()
        {
            client.Dispose();
            server.Dispose();
        }
    }
}
