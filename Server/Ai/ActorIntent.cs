using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// What one actor is doing this tick — as a VALUE, chosen once and consumed once.
///
/// This type exists because of a failure pattern, not a preference for unions. Every bug in this
/// system so far has been the same shape: a decision made in one place and silently overridden in
/// another. A cover gate vetoing the squad's movement order. Two blackboard permits vetoing the
/// squad's firing and movement orders. An entrenchment flag vetoing a bound. An individual's own
/// contact state vetoing a squad manoeuvre. Eight of them in one pass.
///
/// All eight were possible because "should this man move?" was not a decision anybody held — it was
/// a question recomputed from a dozen booleans at each point of use, so any of those points could
/// answer differently. Fifteen interacting booleans is thirty-two thousand nominal states, and the
/// bugs were always unintended combinations rather than wrong values.
///
/// A closed union has exactly one inhabitant at a time. Overriding it requires pattern-matching it
/// away, which is visible in a diff in a way that `&amp;&amp; !mustEntrench` is not. And a `switch`
/// over a sealed hierarchy with no default arm makes the compiler enforce that every consumer
/// handles every case — see the CS8509 promotion in DemiurgeServer.csproj.
/// </summary>
internal abstract record ActorIntent
{
    /// <summary>
    /// This intent's name for the <c>ai track states</c> overlay. Abstract rather than a switch
    /// somewhere else because the compiler cannot prove this hierarchy is closed — CS8509 demands a
    /// default arm and a default arm is exactly the silent hole this type exists to prevent. As a
    /// member, a new case does not compile until it says what it is called.
    ///
    /// Read only by <see cref="MobDebugFeed"/>. Nothing may branch on it: that would make the label
    /// a second copy of the decision.
    /// </summary>
    internal abstract string DebugLabel { get; }

    /// <summary>Stay put and shoot. The base of fire, and the safe default: it is what an actor does
    /// when nothing better prices out, and it is never a deadlock.</summary>
    internal sealed record HoldAndFire : ActorIntent
    {
        internal override string DebugLabel => "HOLD";
    }

    /// <summary>Cross to a new position on a committed bearing while the squad shoots. A SQUAD
    /// decision — it does not require the mover to personally believe there is an enemy, which is
    /// just as well, because a flanker cannot see the man he is flanking.</summary>
    internal sealed record Bound(Vector3 Destination, float Bearing) : ActorIntent
    {
        internal override string DebugLabel => "BOUND";
    }

    /// <summary>Dig a fighting position. Only worth doing when it measurably reduces incoming fire —
    /// a man already behind terrain gains nothing and should be shooting or moving.</summary>
    internal sealed record Entrench : ActorIntent
    {
        internal override string DebugLabel => "ENTRENCH";
    }

    /// <summary>Relocate to terrain that protects better, optionally closing while doing so.</summary>
    internal sealed record SeekCover(bool MayAdvance) : ActorIntent
    {
        internal override string DebugLabel => "COVER";
    }

    /// <summary>
    /// Working a fighting position: digging one, or cycling between its protected centre and its
    /// peek station once it is dug.
    ///
    /// A case rather than a branch below the decision, which is what it used to be. `Entrenching`
    /// and `Entrenched` were tested AFTER an ActorIntent had already been chosen, so an actor could
    /// be told to pursue its objective and then spend the tick in its hole — the decision was not
    /// the decision, which is the exact failure this union exists to prevent. It also made
    /// <see cref="MobDebugFeed"/> lie: `ai track states` drew OBJECTIVE over a man in a foxhole.
    /// </summary>
    internal sealed record HoldFightingPosition : ActorIntent
    {
        internal override string DebugLabel => "DUGIN";
    }

    /// <summary>No believed threat: go where the commander wants this squad to be.</summary>
    internal sealed record PursueObjective : ActorIntent
    {
        internal override string DebugLabel => "OBJECTIVE";
    }
}
