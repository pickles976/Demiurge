using System.Numerics;

namespace Demiurge;

public readonly record struct AiContact(
    ushort ActorId,
    Vector3 Position,
    uint LastSeenTick,
    float Confidence);

/// <summary>
/// What an agent believes, deliberately separate from live server actors. Observations are exact
/// when made, then confidence fades linearly until the contact is forgotten.
/// </summary>
public sealed class ContactMemory
{
    public const int RetentionTicks = 5 * NetworkConfig.TickRate;

    private readonly Dictionary<ushort, Observation> observations = new();

    public int Count => observations.Count;

    public void Observe(ushort actorId, Vector3 position, uint tick)
        => observations[actorId] = new Observation(position, tick);

    public bool TryGet(ushort actorId, uint tick, out AiContact contact)
    {
        if (observations.TryGetValue(actorId, out var observation)
            && Confidence(observation.LastSeenTick, tick) > 0f)
        {
            contact = new AiContact(
                actorId,
                observation.Position,
                observation.LastSeenTick,
                Confidence(observation.LastSeenTick, tick));
            return true;
        }

        contact = default;
        return false;
    }

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
                Confidence(pair.Value.LastSeenTick, tick)))
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
                confidence);
        }
        return nearestDistance < float.MaxValue;
    }

    private static float Confidence(uint seen, uint now)
        => Math.Clamp(1f - (now - seen) / (float)RetentionTicks, 0f, 1f);

    private readonly record struct Observation(Vector3 Position, uint LastSeenTick);
}
