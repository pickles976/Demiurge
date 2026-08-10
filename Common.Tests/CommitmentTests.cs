namespace Demiurge.Tests;

/// <summary>
/// A decision that outlives the pass that made it. Small type, but it replaces three separate
/// stickiness conventions that were each forgotten at least once.
/// </summary>
public class CommitmentTests
{
    [Fact]
    public void NothingCommittedIsDistinguishableFromACommittedDefault()
    {
        Assert.False(Commitment<float>.None.Exists);
        Assert.True(Commitment<float>.For(0f, tick: 10, ticks: 5).Exists);
    }

    [Fact]
    public void ALiveCommitmentIsReadableAndAnExpiredOneIsNot()
    {
        var bearing = Commitment<float>.For(1.05f, tick: 100, ticks: 30);

        Assert.True(bearing.TryGet(129, out float live));
        Assert.Equal(1.05f, live);
        Assert.False(bearing.TryGet(130, out _));
    }

    [Fact]
    public void RenewingKeepsTheOriginalDecisionWhileItStands()
    {
        var bearing = Commitment<float>.For(1.05f, tick: 100, ticks: 30);
        var replanned = bearing.Renew(-1.05f, tick: 110, ticks: 30);

        Assert.True(replanned.TryGet(110, out float value));
        Assert.Equal(1.05f, value);
        Assert.Equal(bearing.ExpiresAtTick, replanned.ExpiresAtTick);
    }

    [Fact]
    public void RenewingAfterExpiryTakesTheNewDecision()
    {
        var bearing = Commitment<float>.For(1.05f, tick: 100, ticks: 30);
        var replanned = bearing.Renew(-1.05f, tick: 130, ticks: 30);

        Assert.True(replanned.TryGet(130, out float value));
        Assert.Equal(-1.05f, value);
    }

    [Fact]
    public void ReleasingEndsItImmediately()
    {
        var bearing = Commitment<float>.For(1.05f, tick: 100, ticks: 300).Released();
        Assert.False(bearing.TryGet(101, out _));
    }

    [Fact]
    public void ACommitmentNeverLastsZeroTicks()
    {
        // Guards the caller who passes a duration computed from something that turned out to be 0 —
        // a commitment that expires on the tick it was made is indistinguishable from none at all,
        // which is the churn this type exists to stop.
        var immediate = Commitment<int>.For(7, tick: 50, ticks: 0);
        Assert.True(immediate.TryGet(50, out int value));
        Assert.Equal(7, value);
    }
}
