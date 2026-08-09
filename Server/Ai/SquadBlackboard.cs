using System.Numerics;

namespace Demiurge.GameServer;

internal readonly record struct SquadObjective(
    uint FlagId,
    Vector3 Position);

internal enum SquadResourceKind : byte
{
    AcquireWeapon,
    OperateMortar,
}

internal readonly record struct SquadResourceObjective(
    SquadResourceKind Kind,
    ushort OperatorId,
    uint ObjectId,
    ItemType Item,
    Vector3 Position,
    Vector3 Target);

/// <summary>
/// Shared, server-only state for one small infantry squad. Reports are delayed before entering the
/// shared contact memory, cover claims are short leases, and engagement tokens rotate so the same
/// two actors do not monopolize fire forever.
/// </summary>
internal sealed class SquadBlackboard
{
    /// <summary>
    /// Men per squad. Six rather than four so a squad can hold a real base of fire AND a real
    /// manoeuvre element at the same time, which is what makes fire-and-movement legible.
    /// </summary>
    public const int MaximumMembers = 6;

    private const int ContactShareDelayTicks =
        (35 * NetworkConfig.TickRate + 99) / 100;
    private const int ClaimLeaseTicks = 2 * NetworkConfig.TickRate;
    private const float ClaimRadius = 1.5f;
    private const float ClaimRadiusSquared = ClaimRadius * ClaimRadius;

    private readonly record struct ContactReport(
        AiContact Contact,
        uint AvailableTick);

    private readonly record struct PositionClaim(
        Vector3 Position,
        uint ExpiresTick);

    // Engagement and advance permits used to live here: rotating two-man leases that decided who was
    // allowed to shoot and who was allowed to close, independently of the squad's actual plan.
    //
    // They were a SECOND arbitration layer, and it disagreed with the first. SquadTactics would assign
    // four men to the base of fire and the blackboard would then forbid two of them from firing, which
    // is the whole point of a base of fire. Measured against a lone rifleman, riflemen stood next to
    // each other holding fire and waiting for a turn that a three-second lease and a one-second
    // cooldown doled out.
    //
    // The allocation is now the only authority: BaseOfFire means shoot, Bound means move. Nothing
    // second-guesses it.

    private readonly Queue<ContactReport> reports = new();
    /// <summary>The squad's collective belief, which outlives any one man's attention. See
    /// ContactMemory.SquadRetentionTicks.</summary>
    private readonly ContactMemory sharedContacts = new(ContactMemory.SquadRetentionTicks);
    private readonly Dictionary<ushort, PositionClaim> claims = new();
    private readonly List<ushort> expiredActors = new(MaximumMembers);
    private readonly List<ushort> roster = new(MaximumMembers);
    private readonly Dictionary<ushort, SquadTacticalOrder> orders = new(MaximumMembers);
    private SquadObjective? objective;
    private SquadResourceObjective? resourceObjective;
    private uint nextGrenadeTick;

    /// <summary>
    /// Live centre of mass. This used to be the mean of members' spawn positions and was never updated,
    /// so every squad-relative calculation -- including the commander's travel costing -- kept measuring
    /// from base long after the squad had advanced.
    /// </summary>
    public Vector3 Centre { get; private set; }

    public int MemberCount => roster.Count;
    public IReadOnlyList<ushort> Roster => roster;
    public uint ObjectiveRevision { get; private set; }
    public uint ResourceRevision { get; private set; }

    /// <summary>Replaces the roster and recomputes the centre from where the members actually are.</summary>
    public void SetRoster(List<ushort> actorIds, Vector3 centre)
    {
        foreach (ushort actorId in roster)
            if (!actorIds.Contains(actorId))
                Release(actorId);
        roster.Clear();
        roster.AddRange(actorIds);
        Centre = centre;

        expiredActors.Clear();
        foreach (var pair in orders)
            if (!roster.Contains(pair.Key))
                expiredActors.Add(pair.Key);
        foreach (ushort actorId in expiredActors)
            orders.Remove(actorId);
    }

    public void SetOrders(List<SquadTacticalOrder> planned)
    {
        orders.Clear();
        foreach (var order in planned)
            orders[order.ActorId] = order;
    }

    public bool TryGetOrder(ushort actorId, out SquadTacticalOrder order)
        => orders.TryGetValue(actorId, out order);

    /// <summary>Drops every lease an actor holds. Called when it leaves the squad or dies.</summary>
    public void Release(ushort actorId)
    {
        claims.Remove(actorId);
        orders.Remove(actorId);
    }

    /// <summary>
    /// The threat the squad is manoeuvring against: the shared contact nearest its centre. One threat
    /// per squad on purpose, because a squad that splits its plan across two enemies does neither.
    /// </summary>
    public bool TryGetPrimaryThreat(uint tick, out AiContact threat)
        => sharedContacts.TryNearest(Centre, tick, out threat);

    public void SetObjective(SquadObjective? value)
    {
        if (objective == value) return;
        objective = value;
        ObjectiveRevision++;
    }

    public bool TryGetObjective(out SquadObjective value)
    {
        if (objective is { } selected)
        {
            value = selected;
            return true;
        }

        value = default;
        return false;
    }

    public void SetResourceObjective(SquadResourceObjective? value)
    {
        if (resourceObjective == value) return;
        resourceObjective = value;
        ResourceRevision++;
    }

    public bool TryGetResourceObjective(ushort actorId, out SquadResourceObjective value)
    {
        if (resourceObjective is { } selected && selected.OperatorId == actorId)
        {
            value = selected;
            return true;
        }

        value = default;
        return false;
    }

    public void Publish(AiContact contact, uint tick)
        => reports.Enqueue(new ContactReport(
            contact,
            tick + ContactShareDelayTicks));

    public void Advance(uint tick)
    {
        while (reports.TryPeek(out var report) && report.AvailableTick <= tick)
        {
            reports.Dequeue();
            sharedContacts.Observe(
                report.Contact.ActorId,
                report.Contact.Position,
                report.Contact.LastSeenTick);
        }
        sharedContacts.Prune(tick);

        PruneClaims(tick);
    }

    public void ShareContactsWith(ContactMemory member, uint tick)
        => sharedContacts.MergeInto(member, tick);

    public bool IsClaimedByOther(ushort actorId, Vector3 position)
    {
        foreach (var pair in claims)
        {
            if (pair.Key == actorId) continue;
            if (HorizontalDistanceSquared(pair.Value.Position, position) < ClaimRadiusSquared)
                return true;
        }
        return false;
    }

    public bool TryClaim(ushort actorId, Vector3 position, uint tick)
    {
        if (IsClaimedByOther(actorId, position)) return false;
        claims[actorId] = new PositionClaim(position, tick + ClaimLeaseTicks);
        return true;
    }

    public void RefreshClaim(ushort actorId, uint tick)
    {
        if (claims.TryGetValue(actorId, out var claim))
            claims[actorId] = claim with { ExpiresTick = tick + ClaimLeaseTicks };
    }

    public void ReleaseClaim(ushort actorId) => claims.Remove(actorId);



    public bool CanReserveGrenade(uint tick) => tick >= nextGrenadeTick;

    public bool TryReserveGrenade(uint tick)
    {
        if (!CanReserveGrenade(tick)) return false;
        nextGrenadeTick =
            tick + GrenadeConfig.FuseTicks + (uint)NetworkConfig.TickRate;
        return true;
    }

    public void CancelGrenadeReservation() => nextGrenadeTick = 0;

    private void PruneClaims(uint tick)
    {
        expiredActors.Clear();
        foreach (var pair in claims)
            if (pair.Value.ExpiresTick < tick)
                expiredActors.Add(pair.Key);
        foreach (ushort actorId in expiredActors)
            claims.Remove(actorId);
    }


    private void PruneCooldowns(
        Dictionary<ushort, uint> cooldowns,
        uint tick)
    {
        expiredActors.Clear();
        foreach (var pair in cooldowns)
            if (pair.Value <= tick)
                expiredActors.Add(pair.Key);
        foreach (ushort actorId in expiredActors)
            cooldowns.Remove(actorId);
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
