using System.Numerics;

namespace Demiurge;

/// <summary>One man a fire mission might be aimed at, as the solver needs to see him.</summary>
/// <param name="Position">Where he is when the mission is planned.</param>
/// <param name="Velocity">How he is moving. This is the only thing that makes a dug-in man a better
/// target than a running one, and it is enough — see <see cref="MortarTargeting"/>.</param>
/// <param name="Value">What his death is worth, in the same tickets everything else is priced in.
/// One for a rifleman; more for a man serving something.</param>
public readonly record struct MortarTarget(
    Vector3 Position,
    Vector3 Velocity,
    float Value = 1f);

/// <summary>
/// Where to drop the next bomb.
///
/// A mortar is not a rifle with a bigger number, and the three complaints it was drawing all came
/// from being aimed like one. It was pointed at <c>SquadBlackboard.TryGetPrimaryThreat</c> — the
/// single contact nearest the firing squad's centre, delayed a third of a second by the reporting
/// model and decayed by the retention model — which is a fine way to choose who to SHOOT and a
/// useless way to choose where to BOMB. The round takes the better part of ten seconds to arrive, it
/// lands somewhere inside a five-metre gaussian, and it kills over fifteen. Aiming it at one moving
/// individual asks it to do the one thing it cannot.
///
/// So this scores PLACES, and the three behaviours that were asked for fall out of one number rather
/// than being three branches:
///
///  - <b>Clusters.</b> The score at a point is the sum over everybody it reaches. Three men standing
///    together are worth three times one man standing alone, without anything in the code knowing
///    what a cluster is.
///  - <b>Dug-in units.</b> A stationary man is exactly where he was when the mission was planned; a
///    man crossing open ground is not, and the round has to be lucky twice. That is priced as
///    <see cref="Coverage"/> uncertainty, so entrenchment is rewarded because it is PREDICTABLE
///    rather than because anything asked whether he was in a hole. A man pinned in a foxhole by
///    direct fire is the same case and gets the same answer.
///  - <b>Counter-battery.</b> A crew served weapon is a stationary target — the best kind here — and
///    the man on it is worth more than a rifleman because killing him also takes the tube off the
///    line. Both terms already exist, so counter-battery is a weight, not a mode.
///
/// Friendly casualties are subtracted rather than vetoed. A ticket is a ticket whoever spends it, so
/// the same sum decides, and a round that kills three of theirs thirty metres from one of ours is
/// correctly a good round. The veto it replaces could not express that and refused every mission
/// with a friendly anywhere near the target.
///
/// Pure, so it is testable without a server, and cheap: candidates are the targets themselves, which
/// is O(n²) over the enemies in range of one tube — a few hundred operations once a second.
/// </summary>
public static class MortarTargeting
{
    /// <summary>
    /// What a man serving a crew weapon is worth over a rifleman.
    ///
    /// Killing him costs the enemy the same one ticket, and additionally takes their tube out of
    /// action for as long as it takes another man to walk to it — at
    /// <see cref="MortarConfig.CarryMoveSpeedScale"/>, that is the expensive part. Three is a
    /// judgement about how much that is worth, and it is the only number here that is one.
    /// </summary>
    public const float CrewServedWeaponValue = 3f;

    /// <summary>
    /// The least a mission has to be worth before it is worth firing.
    ///
    /// A tube that fires at anything wastes a five-second reload on a round with nobody under it, and
    /// gives its position away for free. Half a man is the bar: a round expected to catch at least
    /// that much of somebody.
    /// </summary>
    public const float MinimumMissionValue = 0.5f;

    /// <summary>
    /// Expected fraction of the blast this man catches from a round aimed at <paramref name="aim"/>.
    ///
    /// Two terms and one meaning. How far INSIDE the burst he is falls to nothing at the edge, as the
    /// blast profile itself does. How much the burst can cover of the error the round will actually
    /// have — dispersion, plus however far he will have walked by the time it lands — is the area
    /// ratio between the two, and it is what makes a moving man a worse aim point than a still one
    /// standing the same distance away. Without the second term a wider spread makes every target
    /// look better, because the falloff gets gentler.
    /// </summary>
    public static float Coverage(Vector3 aim, Vector3 position, float spread, in BlastProfile blast)
    {
        float reach = blast.DamageRadius + MathF.Max(0f, spread);
        float distance = Horizontal(aim, position);
        if (distance >= reach) return 0f;

        float certainty = blast.DamageRadius / reach;
        return (1f - distance / reach) * certainty * certainty;
    }

    /// <summary>
    /// How far off the round will be, in metres: its own scatter and his head start, added because
    /// they are independent errors and this is not the place for a convolution nobody asked for.
    /// </summary>
    public static float SpreadFor(in MortarTarget target, float flightSeconds)
        => MortarConfig.DispersionMetres
           + Horizontal(target.Velocity, Vector3.Zero) * MathF.Max(0f, flightSeconds);

    /// <summary>
    /// The best point this tube can drop a bomb on, and what it is worth.
    ///
    /// <paramref name="flightSeconds"/> is how long the round is in the air; pass
    /// <see cref="MortarBallistics.FlightSeconds"/> for the range being considered, or a
    /// representative value when one solution has to serve several candidates.
    ///
    /// Returns false when nothing clears <see cref="MinimumMissionValue"/> — which is the decision
    /// to hold fire, not a failure.
    /// </summary>
    public static bool TrySolve(
        Vector3 tube,
        float tubeYaw,
        float flightSeconds,
        IReadOnlyList<MortarTarget> enemies,
        IReadOnlyList<Vector3> friendlies,
        out Vector3 aim,
        out float value)
    {
        var blast = MortarConfig.Blast;
        aim = default;
        value = 0f;

        for (int i = 0; i < enemies.Count; i++)
        {
            // Aim where he WILL be. A candidate is a place, and the places worth considering are the
            // ones somebody is going to be standing in when the round arrives.
            var candidate = Lead(enemies[i], flightSeconds);
            if (!MortarBallistics.IsTargetInFireSector(tube, tubeYaw, candidate))
                continue;

            float score = 0f;
            for (int j = 0; j < enemies.Count; j++)
                score += enemies[j].Value
                    * Coverage(
                        candidate,
                        Lead(enemies[j], flightSeconds),
                        SpreadFor(enemies[j], flightSeconds),
                        blast);

            // Our own men, at the same price. They are not going anywhere useful in the flight time
            // either, so they are costed where they stand — pessimistically, which is the right
            // direction for the error to run.
            for (int j = 0; j < friendlies.Count; j++)
                score -= Coverage(
                    candidate,
                    friendlies[j],
                    MortarConfig.DispersionMetres,
                    blast);

            if (score <= value) continue;
            value = score;
            aim = candidate;
        }

        if (value >= MinimumMissionValue) return true;
        aim = default;
        value = 0f;
        return false;
    }

    private static Vector3 Lead(in MortarTarget target, float flightSeconds)
        => target.Position + target.Velocity with { Y = 0f } * MathF.Max(0f, flightSeconds);

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
