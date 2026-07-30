using System.Numerics;

namespace Demiurge.GameServer;

internal readonly record struct SquadObjective(
    uint FlagId,
    Vector3 Position);

/// <summary>
/// Shared, server-only state for one small infantry squad. Reports are delayed before entering the
/// shared contact memory, cover claims are short leases, and engagement tokens rotate so the same
/// two actors do not monopolize fire forever.
/// </summary>
internal sealed class SquadBlackboard
{
    public const int MaximumMembers = 4;
    public const int MaximumEngagementTokens = 2;
    public const int MaximumAdvanceTokens = 2;

    private const int ContactShareDelayTicks =
        (35 * NetworkConfig.TickRate + 99) / 100;
    private const int ClaimLeaseTicks = 2 * NetworkConfig.TickRate;
    private const int EngagementTurnTicks = 3 * NetworkConfig.TickRate;
    private const int EngagementRefreshGraceTicks = 2;
    private const int EngagementCooldownTicks = NetworkConfig.TickRate;
    private const float ClaimRadius = 1.5f;
    private const float ClaimRadiusSquared = ClaimRadius * ClaimRadius;

    private readonly record struct ContactReport(
        AiContact Contact,
        uint AvailableTick);

    private readonly record struct PositionClaim(
        Vector3 Position,
        uint ExpiresTick);

    private readonly record struct TokenLease(
        uint StartedTick,
        uint LastRefreshTick);

    private readonly Queue<ContactReport> reports = new();
    private readonly ContactMemory sharedContacts = new();
    private readonly Dictionary<ushort, PositionClaim> claims = new();
    private readonly Dictionary<ushort, TokenLease> engagementTokens = new();
    private readonly Dictionary<ushort, uint> engagementCooldowns = new();
    private readonly Dictionary<ushort, TokenLease> advanceTokens = new();
    private readonly Dictionary<ushort, uint> advanceCooldowns = new();
    private readonly List<ushort> expiredActors = new(MaximumMembers);
    private Vector3 homeSum;
    private int homeCount;
    private SquadObjective? objective;
    private uint nextGrenadeTick;

    public Vector3 Home => homeCount > 0 ? homeSum / homeCount : Vector3.Zero;
    public uint ObjectiveRevision { get; private set; }

    public void AddMemberHome(Vector3 position)
    {
        homeSum += position;
        homeCount++;
    }

    public void RemoveMember(ushort actorId, Vector3 home)
    {
        claims.Remove(actorId);
        engagementTokens.Remove(actorId);
        engagementCooldowns.Remove(actorId);
        advanceTokens.Remove(actorId);
        advanceCooldowns.Remove(actorId);
        if (homeCount <= 0) return;
        homeSum -= home;
        homeCount--;
    }

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
        PruneTokens(engagementTokens, tick);
        PruneTokens(advanceTokens, tick);
        PruneCooldowns(engagementCooldowns, tick);
        PruneCooldowns(advanceCooldowns, tick);
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

    public bool TryAcquireEngagement(ushort actorId, uint tick)
        => TryAcquireToken(
            actorId,
            tick,
            engagementTokens,
            engagementCooldowns,
            MaximumEngagementTokens);

    public bool TryAcquireAdvance(ushort actorId, uint tick)
        => TryAcquireToken(
            actorId,
            tick,
            advanceTokens,
            advanceCooldowns,
            MaximumAdvanceTokens);

    public void ReleaseEngagement(ushort actorId)
        => engagementTokens.Remove(actorId);

    public void ReleaseAdvance(ushort actorId)
        => advanceTokens.Remove(actorId);

    public bool CanReserveGrenade(uint tick) => tick >= nextGrenadeTick;

    public bool TryReserveGrenade(uint tick)
    {
        if (!CanReserveGrenade(tick)) return false;
        nextGrenadeTick =
            tick + GrenadeConfig.FuseTicks + (uint)NetworkConfig.TickRate;
        return true;
    }

    public void CancelGrenadeReservation() => nextGrenadeTick = 0;

    private static bool TryAcquireToken(
        ushort actorId,
        uint tick,
        Dictionary<ushort, TokenLease> tokens,
        Dictionary<ushort, uint> cooldowns,
        int maximumTokens)
    {
        if (tokens.TryGetValue(actorId, out var lease))
        {
            if (tick - lease.StartedTick < EngagementTurnTicks)
            {
                tokens[actorId] = lease with { LastRefreshTick = tick };
                return true;
            }

            tokens.Remove(actorId);
            cooldowns[actorId] = tick + EngagementCooldownTicks;
            return false;
        }

        if (cooldowns.TryGetValue(actorId, out uint cooldown) && tick < cooldown)
            return false;
        if (tokens.Count >= maximumTokens)
            return false;

        tokens[actorId] = new TokenLease(tick, tick);
        return true;
    }

    private void PruneClaims(uint tick)
    {
        expiredActors.Clear();
        foreach (var pair in claims)
            if (pair.Value.ExpiresTick < tick)
                expiredActors.Add(pair.Key);
        foreach (ushort actorId in expiredActors)
            claims.Remove(actorId);
    }

    private void PruneTokens(
        Dictionary<ushort, TokenLease> tokens,
        uint tick)
    {
        expiredActors.Clear();
        foreach (var pair in tokens)
            if (tick - pair.Value.LastRefreshTick > EngagementRefreshGraceTicks)
                expiredActors.Add(pair.Key);
        foreach (ushort actorId in expiredActors)
            tokens.Remove(actorId);
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
