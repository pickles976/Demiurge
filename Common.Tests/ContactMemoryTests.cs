using System.Numerics;

namespace Demiurge.Tests;

public class ContactMemoryTests
{
    [Fact]
    public void ObservationFadesAndExpiresUsingServerTicks()
    {
        var memory = new ContactMemory();
        var position = new Vector3(4, 5, 6);
        memory.Observe(7, position, tick: 10);

        Assert.True(memory.TryGet(7, 10, out var fresh));
        Assert.Equal(1f, fresh.Confidence);
        Assert.Equal(position, fresh.Position);

        uint halfway = 10 + ContactMemory.RetentionTicks / 2;
        Assert.True(memory.TryGet(7, halfway, out var faded));
        Assert.Equal(0.5f, faded.Confidence, 2);

        memory.Prune(10 + ContactMemory.RetentionTicks);
        Assert.False(memory.TryGet(7, 10 + ContactMemory.RetentionTicks, out _));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void SeeingActorAgainRefreshesPositionAndConfidence()
    {
        var memory = new ContactMemory();
        memory.Observe(2, Vector3.Zero, 0);
        memory.Observe(2, Vector3.One, NetworkConfig.TickRate);

        var contact = Assert.Single(memory.Snapshot(NetworkConfig.TickRate));

        Assert.Equal(Vector3.One, contact.Position);
        Assert.Equal(1f, contact.Confidence);
    }

    [Fact]
    public void NearestBelievedContactIsSelectedWithoutReadingLiveActors()
    {
        var memory = new ContactMemory();
        memory.Observe(9, new Vector3(20, 0, 0), 5);
        memory.Observe(4, new Vector3(3, 0, 0), 5);

        Assert.True(memory.TryNearest(Vector3.Zero, 5, out var nearest));
        Assert.Equal((ushort)4, nearest.ActorId);
    }

    [Fact]
    public void OlderSharedReportCannotOverwriteNewerDirectSighting()
    {
        var local = new ContactMemory();
        var shared = new ContactMemory();
        local.Observe(4, new Vector3(10, 0, 0), tick: 20);
        shared.Observe(4, new Vector3(2, 0, 0), tick: 10);

        shared.MergeInto(local, tick: 20);

        Assert.True(local.TryGet(4, 20, out var contact));
        Assert.Equal(new Vector3(10, 0, 0), contact.Position);
        Assert.Equal((uint)20, contact.LastSeenTick);
    }
}
