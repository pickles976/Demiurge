using System.Numerics;

namespace Demiurge;

public readonly record struct AiContact(
    ushort ActorId,
    Vector3 Position,
    uint LastSeenTick,
    float Confidence,
    ItemType? ObservedWeapon = null,
    float TargetingLikelihood = 0.5f,
    float ObservedExtraMoa = 0f);

/// <summary>
/// What an agent believes, deliberately separate from live server actors. Observations are exact
/// when made, then confidence fades linearly until the contact is forgotten.
/// </summary>
public sealed class ContactMemory
{
    /// <summary>How long ONE man keeps believing in an enemy he can no longer see.</summary>
    public const int RetentionTicks = 5 * NetworkConfig.TickRate;

    /// <summary>
    /// How long a SQUAD keeps believing, which is deliberately much longer.
    ///
    /// A squad's belief is not one man's attention and must not decay like it. Perception has a 110
    /// degree field of view, so a man who turns to run a flank stops seeing the enemy he is flanking
    /// — and with a single five-second memory shared by everyone, two movers looking away was enough
    /// to make the whole squad forget. Losing the contact then cancelled the manoeuvre, which turned
    /// them back toward the threat, which reacquired it: measured, a six-man assault advanced for
    /// twenty seconds and then fell back, with 36% of actor-ticks believing in no enemy at all.
    ///
    /// Twenty seconds is long enough to cross the ground a bound covers, and short enough that a
    /// squad still eventually loses a man who has genuinely broken contact.
    /// </summary>
    public const int SquadRetentionTicks = 20 * NetworkConfig.TickRate;

    private readonly int retentionTicks;
    private readonly Dictionary<ushort, Observation> observations = new();

    public ContactMemory(int retentionTicks = RetentionTicks) => this.retentionTicks = retentionTicks;

    public int Count => observations.Count;

    public void Observe(
        ushort actorId,
        Vector3 position,
        uint tick,
        ItemType? observedWeapon = null,
        float targetingLikelihood = 0.5f,
        float observedExtraMoa = 0f)
    {
        if (!observations.TryGetValue(actorId, out var existing)
            || tick >= existing.LastSeenTick)
            observations[actorId] = new Observation(
                position,
                tick,
                observedWeapon ?? existing.ObservedWeapon,
                Math.Clamp(targetingLikelihood, 0f, 1f),
                MathF.Max(0f, observedExtraMoa));
    }

    /// <summary>
    /// Copies still-believed observations without allocating a snapshot. Older squad reports cannot
    /// overwrite a newer direct sighting because <see cref="Observe"/> is monotonic by sighting tick.
    /// </summary>
    public void MergeInto(ContactMemory target, uint tick)
    {
        foreach (var pair in observations)
        {
            if (Confidence(pair.Value.LastSeenTick, tick) <= 0f) continue;
            target.Observe(
                pair.Key,
                pair.Value.Position,
                pair.Value.LastSeenTick,
                pair.Value.ObservedWeapon,
                pair.Value.TargetingLikelihood,
                pair.Value.ObservedExtraMoa);
        }
    }

    public bool TryGet(ushort actorId, uint tick, out AiContact contact)
    {
        if (observations.TryGetValue(actorId, out var observation)
            && Confidence(observation.LastSeenTick, tick) > 0f)
        {
            contact = new AiContact(
                actorId,
                observation.Position,
                observation.LastSeenTick,
                Confidence(observation.LastSeenTick, tick),
                observation.ObservedWeapon,
                observation.TargetingLikelihood,
                observation.ObservedExtraMoa);
            return true;
        }

        contact = default;
        return false;
    }

    /// <summary>Forgets everything. Death is the one event that genuinely clears a belief.</summary>
    public void Forget() => observations.Clear();

    public void Prune(uint tick)
    {
        List<ushort>? expired = null;
        foreach (var pair in observations)
            if (Confidence(pair.Value.LastSeenTick, tick) <= 0f)
                (expired ??= []).Add(pair.Key);
        if (expired is null) return;
        foreach (ushort actorId in expired)
            observations.Remove(actorId);
    }

    public IReadOnlyList<AiContact> Snapshot(uint tick)
        => observations
            .Select(pair => new AiContact(
                pair.Key,
                pair.Value.Position,
                pair.Value.LastSeenTick,
                Confidence(pair.Value.LastSeenTick, tick),
                pair.Value.ObservedWeapon,
                pair.Value.TargetingLikelihood,
                pair.Value.ObservedExtraMoa))
            .Where(contact => contact.Confidence > 0f)
            .OrderBy(contact => contact.ActorId)
            .ToArray();

    public bool TryNearest(Vector3 origin, uint tick, out AiContact nearest)
    {
        nearest = default;
        float nearestDistance = float.MaxValue;
        foreach (var pair in observations)
        {
            float confidence = Confidence(pair.Value.LastSeenTick, tick);
            if (confidence <= 0f) continue;
            float distance = Vector3.DistanceSquared(origin, pair.Value.Position);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = new AiContact(
                pair.Key,
                pair.Value.Position,
                pair.Value.LastSeenTick,
                confidence,
                pair.Value.ObservedWeapon,
                pair.Value.TargetingLikelihood,
                pair.Value.ObservedExtraMoa);
        }
        return nearestDistance < float.MaxValue;
    }

    private float Confidence(uint seen, uint now)
        => Math.Clamp(1f - (now - seen) / (float)retentionTicks, 0f, 1f);

    private readonly record struct Observation(
        Vector3 Position,
        uint LastSeenTick,
        ItemType? ObservedWeapon,
        float TargetingLikelihood,
        float ObservedExtraMoa);
}
