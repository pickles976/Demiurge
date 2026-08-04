using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>Budgeted enemy sensing. At most one terrain LOS ray is cast per mob per server tick.</summary>
internal sealed class Perception
{
    private const float MaximumSightDistance = 100f;
    private const float FieldOfViewDegrees = 110f;
    private static readonly float MinimumViewDot =
        MathF.Cos(FieldOfViewDegrees * 0.5f * MathF.PI / 180f);

    /// <summary>Centre mass was reachable: the whole silhouette is available to shoot at.</summary>
    private static readonly TargetExposure FullyExposed = TargetExposure.Full;

    /// <summary>
    /// Only the peek height answered, so the target is behind something with its head and shoulders
    /// over the top. A fifth of a standing man's presented area, which is what makes shooting at a
    /// man in a trench genuinely poor value rather than merely slightly worse.
    /// </summary>
    private static readonly TargetExposure PeekingExposure = TargetExposure.Of(0.2f);

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
            // Range and field of view are judged against centre mass. Only the line of sight probe
            // varies by body point, because which PART of a target is exposed is a cover question
            // rather than a sensing one.
            Vector3 centre = target.Position + Vector3.UnitY * GunConfig.PlayerCenterHeight;
            Vector3 toCentre = centre - origin;
            float distance = toCentre.Length();
            if (distance <= 1e-5f || distance > MaximumSightDistance)
                continue;

            Vector3 flat = toCentre with { Y = 0f };
            if (flat.LengthSquared() <= 1e-6f) continue;
            flat = Vector3.Normalize(flat);
            var facing = new Vector3(MathF.Sin(observer.Yaw), 0f, MathF.Cos(observer.Yaw));
            if (Vector3.Dot(facing, flat) < MinimumViewDot)
                continue;

            brain.PerceptionCursor = (candidateIndex + 1) % actors.Count;

            // Centre mass first, then the head. Probing only centre mass is what made a target
            // peeking over cover with just its head exposed completely invisible: the ray drove into
            // the cover and the NPC never acquired a contact, so it never returned fire. The second
            // ray is only paid when centre mass is genuinely blocked, which is the uncommon case.
            // Whichever point answered is remembered so CombatBehavior aims where perception saw.
            for (int heightIndex = 0; heightIndex < GunConfig.AimHeights.Length; heightIndex++)
            {
                float height = GunConfig.AimHeights[heightIndex];
                Vector3 aim = target.Position + Vector3.UnitY * height;
                Vector3 delta = aim - origin;
                float aimDistance = delta.Length();
                if (aimDistance <= 1e-5f) continue;

                var hit = TerrainRaycast.Cast(terrain, origin, delta, aimDistance);
                if (hit is { } blocked && blocked.Distance < aimDistance - 0.1f) continue;

                brain.Contacts.Observe(target.Id, target.Position, tick);
                brain.PerceivedTargetId = target.Id;
                brain.PerceivedAimHeight = height;

                // Exposure, for free, from which aim point answered. Centre mass clear means the
                // whole silhouette is available; only the peek height clear means head and shoulders
                // over cover. It feeds the target radius in WeaponEffectiveness, so a man behind a
                // parapet is genuinely harder to hit and not merely harder to see — and it costs no
                // rays beyond the ones perception was already casting.
                brain.PerceivedExposure = heightIndex == 0
                    ? FullyExposed
                    : PeekingExposure;

                observed = new AiContact(target.Id, target.Position, tick, 1f);
                break;
            }
            return true;
        }

        return false;
    }
}
