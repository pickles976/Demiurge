using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Throws at a recently lost contact only when terrain blocks the direct shot and a low ballistic
/// arc reaches a safe point behind that cover. The target remains belief state, never a live enemy
/// lookup; live actors are consulted only to avoid friendly blast damage.
/// </summary>
internal sealed class GrenadeBehavior
{
    private const int LostSightDelayTicks = 4;
    private const int MaximumContactAgeTicks = 5 * NetworkConfig.TickRate / 2;
    private const int DecisionIntervalTicks = NetworkConfig.TickRate / 5;
    private const float CoverProbeDistance = 2.5f;
    private const float MinimumThrowRange = GrenadeConfig.DamageRadius + 1f;
    private const float MaximumThrowRange = 30f;
    private const float FriendlySafetyRadius = GrenadeConfig.DamageRadius + 1f;
    private const int TrajectorySamples = 12;
    private const float LandingTolerance = 1.5f;

    private readonly ChunkMap terrain;
    private readonly GrenadeSystem grenades;

    public GrenadeBehavior(ChunkMap terrain, GrenadeSystem grenades)
    {
        this.terrain = terrain;
        this.grenades = grenades;
    }

    public bool TryThrow(
        ServerPlayer mob,
        MobBrain brain,
        SquadBlackboard squad,
        ICollection<ServerPlayer> actors,
        uint tick)
    {
        if (!mob.Move.Grounded
            || tick < mob.ReloadDoneTick
            || tick < brain.NextGrenadeDecisionTick
            || !squad.CanReserveGrenade(tick))
            return false;
        brain.NextGrenadeDecisionTick = tick + DecisionIntervalTicks;

        if (!brain.Contacts.TryNearest(mob.Position, tick, out var contact))
            return false;
        uint age = tick - contact.LastSeenTick;
        if (age < LostSightDelayTicks || age > MaximumContactAgeTicks)
            return false;

        HotbarSlot previousHotbar = mob.Hotbar;
        mob.Hotbar = HotbarSlot.Grenade;
        if (!grenades.CanThrow(mob, tick))
        {
            mob.Hotbar = previousHotbar;
            return false;
        }

        float eyeHeight = mob.State.HasFlag(PlayerStateFlags.Crouching)
            ? Digging.EyeHeight - PlayerMovement.CrouchEyeDrop
            : Digging.EyeHeight;
        Vector3 origin = mob.Position + Vector3.UnitY * eyeHeight;
        Vector3 away = contact.Position - mob.Position;
        away.Y = 0f;
        if (away.LengthSquared() <= 1e-6f)
        {
            mob.Hotbar = previousHotbar;
            return false;
        }
        away = Vector3.Normalize(away);

        Vector3 behindCover = contact.Position + away * CoverProbeDistance;
        if (!TrySolution(origin, behindCover, mob.Team, actors, out var solution)
            && !TrySolution(origin, contact.Position, mob.Team, actors, out solution))
        {
            mob.Hotbar = previousHotbar;
            return false;
        }

        if (!squad.TryReserveGrenade(tick))
        {
            mob.Hotbar = previousHotbar;
            return false;
        }

        bool thrown = grenades.ApplyThrow(mob, new PlayerFireData
        {
            Sequence = ++brain.ShotSequence,
            Origin = origin,
            Direction = solution.Direction,
            RenderTick = tick,
            Hotbar = HotbarSlot.Grenade,
        }, tick);
        if (!thrown)
        {
            squad.CancelGrenadeReservation();
            mob.Hotbar = previousHotbar;
        }
        return thrown;
    }

    private bool TrySolution(
        Vector3 origin,
        Vector3 requestedTarget,
        int friendlyTeam,
        ICollection<ServerPlayer> actors,
        out ThrowSolution solution)
    {
        solution = default;
        Vector3 surface = SurfaceQuery.SurfacePosition(
            terrain,
            requestedTarget.X,
            requestedTarget.Z);
        Vector3 target = surface + Vector3.UnitY * (GrenadeConfig.Radius * 2f);
        Vector3 direct = target - origin;
        float distance = direct.Length();
        if (distance < MinimumThrowRange || distance > MaximumThrowRange)
            return false;

        var obstruction = TerrainRaycast.Cast(
            terrain,
            origin,
            direct / distance,
            distance);
        if (obstruction is not { } wall || wall.Distance >= distance - LandingTolerance)
            return false;

        foreach (var actor in actors)
        {
            if (actor.Team != friendlyTeam
                || actor.Status is not { Health.Current: > 0 })
                continue;
            if (Vector3.DistanceSquared(actor.Position, target)
                < FriendlySafetyRadius * FriendlySafetyRadius)
                return false;
        }

        return ThrowSolver.TryLowArc(
                   origin,
                   target,
                   GrenadeConfig.ThrowSpeed,
                   GrenadeConfig.Gravity,
                   out solution)
               && TrajectoryIsClear(origin, target, solution);
    }

    private bool TrajectoryIsClear(
        Vector3 origin,
        Vector3 target,
        ThrowSolution solution)
    {
        Vector3 previous = origin;
        for (int sample = 1; sample <= TrajectorySamples; sample++)
        {
            float time = solution.FlightSeconds * sample / TrajectorySamples;
            Vector3 next = ThrowSolver.PositionAt(
                origin,
                solution.Direction,
                GrenadeConfig.ThrowSpeed,
                GrenadeConfig.Gravity,
                time);
            Vector3 segment = next - previous;
            float length = segment.Length();
            if (length > 1e-5f)
            {
                var hit = TerrainRaycast.Cast(
                    terrain,
                    previous,
                    segment / length,
                    length);
                if (hit is { } contact)
                {
                    bool intendedLanding =
                        sample >= TrajectorySamples - 1
                        && Vector3.DistanceSquared(contact.Point, target)
                            <= LandingTolerance * LandingTolerance;
                    return intendedLanding;
                }
            }
            previous = next;
        }
        return true;
    }
}
