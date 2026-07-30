using System.Numerics;

namespace Demiurge.GameServer;

internal enum SquadRole : byte
{
    /// <summary>No believed threat. The member follows its objective normally.</summary>
    None,

    /// <summary>
    /// Hold, keep the threat's head down, and dig a fighting position if the terrain does not already
    /// provide one. Someone must be in this role before anyone is allowed to move.
    /// </summary>
    BaseOfFire,

    /// <summary>Move to an assigned envelope position while the rest of the squad shoots.</summary>
    Bound,
}

/// <summary>Which side of the threat axis a member works, kept stable across replans.</summary>
internal enum FlankSide : byte
{
    None,
    Left,
    Right,
}

internal readonly record struct SquadTacticalInput(
    ushort ActorId,
    Vector3 Position,
    FlankSide Side,
    int BoundIndex,
    /// <summary>In position and able to shoot: at cover, or dug into a fighting position.</summary>
    bool IsSet);

internal readonly record struct SquadTacticalOrder(
    ushort ActorId,
    SquadRole Role,
    FlankSide Side,
    Vector3 Destination,
    int BoundIndex);

/// <summary>
/// Turns a believed threat into per-member roles. This is the layer that did not exist: the blackboard
/// arbitrated resources (two engagement tokens, two advance tokens, position claims) while every NPC
/// independently decided to close on the threat, which is why a squad read as N individuals converging
/// on one point instead of a squad manoeuvring.
///
/// Pure and deterministic so the doctrine can be tested headlessly. Terrain is deliberately absent:
/// this produces intent, and navigation and cover selection resolve it against the actual field.
/// </summary>
internal static class SquadTactics
{
    /// <summary>Standoff for the first bound, and how much closer each subsequent bound gets.</summary>
    public const float OpeningStandoff = 45f;
    public const float BoundLength = 12f;
    public const float MinimumStandoff = 12f;

    /// <summary>
    /// Lateral offset of an envelope position from the threat axis. Wide enough that the two sides
    /// approach on visibly different bearings, which is the whole point of enveloping: the player
    /// cannot hold both with one arc of fire.
    /// </summary>
    public const float EnvelopeWidth = 22f;

    /// <summary>
    /// Movers allowed per side at once. One is what makes it a bound rather than a charge: the rest of
    /// the side is shooting while he moves.
    /// </summary>
    public const int MoversPerSide = 1;

    public static void Plan(
        Vector3 threat,
        bool hasThreat,
        IReadOnlyList<SquadTacticalInput> members,
        List<SquadTacticalOrder> orders)
    {
        orders.Clear();
        if (members.Count == 0) return;

        if (!hasThreat)
        {
            foreach (var member in members)
                orders.Add(new SquadTacticalOrder(
                    member.ActorId,
                    SquadRole.None,
                    FlankSide.None,
                    Vector3.Zero,
                    member.BoundIndex));
            return;
        }

        var ordered = new List<SquadTacticalInput>(members);
        ordered.Sort(static (left, right) => left.ActorId.CompareTo(right.ActorId));

        Vector3 centre = Vector3.Zero;
        foreach (var member in ordered) centre += member.Position;
        centre /= ordered.Count;

        Vector3 axis = Horizontal(threat - centre);
        axis = axis.LengthSquared() <= 1e-6f
            ? Vector3.UnitZ
            : Vector3.Normalize(axis);
        Vector3 lateral = LateralAxis(axis);

        // Sides are sticky. A member that has already committed to going left keeps going left, so a
        // replan mid-manoeuvre does not send it back across the axis through the beaten zone.
        var sides = new Dictionary<ushort, FlankSide>(ordered.Count);
        int unassignedRank = 0;
        foreach (var member in ordered)
        {
            if (member.Side != FlankSide.None)
            {
                sides[member.ActorId] = member.Side;
                continue;
            }
            sides[member.ActorId] = unassignedRank++ % 2 == 0
                ? FlankSide.Left
                : FlankSide.Right;
        }

        // Nobody moves until somebody is shooting. With no one set the whole squad goes to ground and
        // digs in first, which is the "he digs in before anything else happens" step.
        bool anySet = false;
        foreach (var member in ordered)
            if (member.IsSet) { anySet = true; break; }

        // One mover per side: the man farthest from the threat. Leapfrog is emergent rather than a
        // state machine — once he bounds past his partner, the partner becomes the farthest and takes
        // the next bound, and they alternate for as long as the fight lasts.
        var movers = new HashSet<ushort>();
        if (anySet)
            foreach (var side in (ReadOnlySpan<FlankSide>)[FlankSide.Left, FlankSide.Right])
            {
                var candidates = new List<SquadTacticalInput>();
                foreach (var member in ordered)
                    if (sides[member.ActorId] == side)
                        candidates.Add(member);
                if (candidates.Count == 0) continue;

                // A lone set member on a side may not abandon overwatch unless the other side has it.
                bool coveredElsewhere = false;
                foreach (var member in ordered)
                    if (sides[member.ActorId] != side && member.IsSet)
                        coveredElsewhere = true;

                candidates.Sort((left, right) =>
                {
                    int byDistance = DistanceSquared(right.Position, threat)
                        .CompareTo(DistanceSquared(left.Position, threat));
                    return byDistance != 0 ? byDistance : left.ActorId.CompareTo(right.ActorId);
                });

                int selected = 0;
                foreach (var candidate in candidates)
                {
                    if (selected >= MoversPerSide) break;
                    bool wouldStripSideOfFire =
                        candidate.IsSet
                        && CountSet(candidates) <= 1
                        && !coveredElsewhere;
                    if (wouldStripSideOfFire) continue;
                    movers.Add(candidate.ActorId);
                    selected++;
                }
            }

        foreach (var member in ordered)
        {
            FlankSide side = sides[member.ActorId];
            bool bounding = movers.Contains(member.ActorId);
            orders.Add(new SquadTacticalOrder(
                member.ActorId,
                bounding ? SquadRole.Bound : SquadRole.BaseOfFire,
                side,
                bounding
                    ? EnvelopePosition(threat, axis, lateral, side, member.BoundIndex)
                    : member.Position,
                member.BoundIndex));
        }
    }

    /// <summary>
    /// Right-hand perpendicular of a threat axis. One definition so the geometry and the Left/Right
    /// labels cannot drift apart: with the threat at +Z this yields +X, so Right really is to the right.
    /// </summary>
    public static Vector3 LateralAxis(Vector3 axis) => new(axis.Z, 0f, -axis.X);

    /// <summary>
    /// Where a bound ends: offset laterally onto this member's side of the threat axis, and closer to
    /// the threat with every completed bound. The lateral term shrinks as the standoff does, so the two
    /// sides converge on the threat instead of walking past it.
    /// </summary>
    public static Vector3 EnvelopePosition(
        Vector3 threat,
        Vector3 axis,
        Vector3 lateral,
        FlankSide side,
        int boundIndex)
    {
        float standoff = MathF.Max(
            MinimumStandoff,
            OpeningStandoff - MathF.Max(0, boundIndex) * BoundLength);
        float closingFraction = OpeningStandoff <= MinimumStandoff
            ? 0f
            : Math.Clamp(
                (standoff - MinimumStandoff) / (OpeningStandoff - MinimumStandoff),
                0f,
                1f);
        float offset = EnvelopeWidth * (0.35f + 0.65f * closingFraction);
        float direction = side == FlankSide.Right ? 1f : -1f;
        return threat - axis * standoff + lateral * (offset * direction);
    }

    private static int CountSet(List<SquadTacticalInput> members)
    {
        int count = 0;
        foreach (var member in members)
            if (member.IsSet) count++;
        return count;
    }

    private static Vector3 Horizontal(Vector3 value) => value with { Y = 0f };

    private static float DistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
