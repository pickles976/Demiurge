using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>Budgeted enemy sensing. At most one terrain LOS ray is cast per mob per server tick.</summary>
internal sealed class Perception
{
    private const float MaximumSightDistance = 100f;
    private const float FieldOfViewDegrees = 110f;
    private static readonly float MinimumViewDot =
        MathF.Cos(FieldOfViewDegrees * 0.5f * MathF.PI / 180f);

    private readonly ChunkMap terrain;

    public Perception(ChunkMap terrain) => this.terrain = terrain;

    public AiContact? Tick(
        ServerPlayer observer,
        ICollection<ServerPlayer> actors,
        MobBrain brain,
        uint tick)
    {
        brain.Contacts.Prune(tick);
        if (actors.Count <= 1) return null;

        int start = brain.PerceptionCursor % actors.Count;
        if (TrySenseRange(
                observer,
                actors,
                brain,
                tick,
                start,
                actors.Count,
                out var observed)
            || TrySenseRange(
                observer,
                actors,
                brain,
                tick,
                0,
                start,
                out observed))
            return observed;

        brain.PerceptionCursor = (start + 1) % actors.Count;
        return null;
    }

    private bool TrySenseRange(
        ServerPlayer observer,
        ICollection<ServerPlayer> actors,
        MobBrain brain,
        uint tick,
        int first,
        int last,
        out AiContact? observed)
    {
        observed = null;
        int index = 0;
        foreach (var target in actors)
        {
            if (index < first)
            {
                index++;
                continue;
            }
            if (index >= last) break;
            int candidateIndex = index++;

            if (target.Id == observer.Id
                || target.Team == observer.Team
                || target.Team <= 0
                || target.Status is { Health.Current: 0 })
                continue;

            float eyeHeight = observer.State.HasFlag(PlayerStateFlags.Crouching)
                ? Digging.EyeHeight - PlayerMovement.CrouchEyeDrop
                : Digging.EyeHeight;
            Vector3 origin = observer.Position + Vector3.UnitY * eyeHeight;
            // Exactly the point CombatBehavior aims at. A higher perception ray could see over a
            // low wall while the actual centre-mass shot still drives into it.
            Vector3 aim = target.Position + Vector3.UnitY * GunConfig.PlayerCenterHeight;
            Vector3 delta = aim - origin;
            float distance = delta.Length();
            if (distance <= 1e-5f || distance > MaximumSightDistance)
                continue;

            Vector3 flat = delta with { Y = 0f };
            if (flat.LengthSquared() <= 1e-6f) continue;
            flat = Vector3.Normalize(flat);
            var facing = new Vector3(MathF.Sin(observer.Yaw), 0f, MathF.Cos(observer.Yaw));
            if (Vector3.Dot(facing, flat) < MinimumViewDot)
                continue;

            brain.PerceptionCursor = (candidateIndex + 1) % actors.Count;
            var hit = TerrainRaycast.Cast(terrain, origin, delta, distance);
            if (hit is null || hit.Value.Distance >= distance - 0.1f)
            {
                brain.Contacts.Observe(target.Id, target.Position, tick);
                observed = new AiContact(target.Id, target.Position, tick, 1f);
            }
            return true;
        }

        return false;
    }
}
