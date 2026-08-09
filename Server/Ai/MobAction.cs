using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Everything an NPC emits for one tick, as one value.
///
/// This exists because <c>MobSystem.Step</c> had five exits — grenade, combat, entrenching,
/// entrenched, path-following — and each one independently constructed the actor's whole output and
/// returned. Whichever was reached first won, and nothing downstream could see that an answer had
/// already been given. <see cref="ActorIntent"/> was introduced to make the DECISION singular; this
/// makes the RESULT singular, which is the other half and the one that was missing.
///
/// A struct with no methods on purpose: a decider builds one and hands it over. It has no access to
/// the actor, so it cannot write to it, so the failure mode is structurally unavailable rather than
/// forbidden by a comment.
/// </summary>
internal readonly record struct MobAction
{
    /// <summary>Normalized movement direction, or zero to stand.</summary>
    public Vector3 Intent { get; init; }

    public bool Jump { get; init; }
    public bool Sprint { get; init; }
    public bool Crouch { get; init; }

    /// <summary>Actuating the held item — a trigger pull or a shovel swing. Both look the same to
    /// the client's view, which is why digging sets it too.</summary>
    public bool Shooting { get; init; }

    public bool Aiming { get; init; }
    public bool Reloading { get; init; }

    /// <summary>Absolute facing, meaningful only when <see cref="TurnTo"/> is set.</summary>
    public float Yaw { get; init; }
    public float Pitch { get; init; }

    /// <summary>Whether <see cref="Yaw"/> and <see cref="Pitch"/> mean anything. A path follower
    /// turns toward its intent and a shooter turns toward its target; a man cycling inside a foxhole
    /// does neither and must not be snapped to zero.</summary>
    public bool TurnTo { get; init; }

    /// <summary>
    /// Standing still on purpose, so the stuck watchdog must not count it against him.
    ///
    /// Here rather than on <see cref="MobBrain"/> because it is a conclusion of THIS tick's decision,
    /// and per-tick state parked on a long-lived object is the same shape as the bug this whole split
    /// exists to remove — one writer sets it, a later reader finds last tick's answer.
    /// </summary>
    public bool HoldingObjective { get; init; }

    /// <summary>This tick's decision moved dirt, which counts as progress even when the actor did
    /// not move. Same reasoning as <see cref="HoldingObjective"/> for why it lives here.</summary>
    public bool TerrainProgress { get; init; }
}
