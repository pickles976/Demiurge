namespace Demiurge.Net
{
    /// <summary>
    /// Delivery guarantee for a message. Deliberately the same two names Riptide uses, because these
    /// are the only two the game has ever sent and renaming them would churn 23 call sites for nothing.
    /// </summary>
    /// <remarks>
    /// Verified against the decompiled <c>Riptide.Connection</c> (2.2.1), because the in-process
    /// transport has to reproduce a superset of these and guessing would defeat the point:
    /// <list type="bullet">
    /// <item><see cref="Reliable"/> — <c>ReliableSequencer.ShouldHandle</c> filters duplicates and lets
    /// out-of-order messages through, against a windowed bitfield that warns past a gap of 64.
    /// So: arrives exactly once, in ARBITRARY order.</item>
    /// <item><see cref="Unreliable"/> — never sequenced at all. The send path assigns no sequence id and
    /// <c>Connection.ShouldHandle</c> consults only the reliable sequencer, so there is no dedup and no
    /// ordering. So: may be dropped, reordered, AND DUPLICATED.</item>
    /// </list>
    /// The duplication case is the one that surprises people. It is real, and no localhost test we had
    /// before this transport existed could produce it.
    /// </remarks>
    public enum MessageSendMode : byte
    {
        /// <summary>May be dropped, reordered, or duplicated. Used for input and state that a later
        /// message supersedes anyway.</summary>
        Unreliable = 0,

        /// <summary>Arrives exactly once, but NOT necessarily in send order. Every reliable message
        /// must be independently applicable.</summary>
        Reliable = 1,
    }
}
