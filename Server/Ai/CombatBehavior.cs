using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// First combat layer: hold position, settle aim, and request authoritative rifle fire against
/// believed contacts. Movement-under-fire and cover selection deliberately belong to later layers.
/// </summary>
internal sealed class CombatBehavior
{
    private const float AimedFireThreshold = 0.55f;
    // Four enemies can focus one exposed player in the demo. This intentionally represents an
    // unsettled combat shooter rather than bench accuracy: about 22% centre-mass chance at 40 m
    // before recoil, while close-range fire remains dangerous.
    private const float AiAimMoa = 360f;
    private const float LongRangeAimMoa = 90f;
    private const float LongRangeStart = 30f;
    private const float LongRangeFullAccuracy = 70f;
    private const float AimToleranceDegrees = 7f;
    private const float LongRangeAimToleranceDegrees = 2.5f;
    private const float AimTurnDegreesPerSecond = 180f;
    private const int SuppressionBurstShots = 3;
    private const int ReactionTicks = (55 * NetworkConfig.TickRate + 99) / 100;
    private const int LostContactHoldTicks = 3 * NetworkConfig.TickRate / 2;
    private const int BurstPauseTicks = 3 * NetworkConfig.TickRate / 4;
    private const int PrecisionShotIntervalTicks = 6 * NetworkConfig.TickRate / 5;
    internal const float PreferredEngagementRange = 25f;
    private static readonly float AimToleranceCos =
        MathF.Cos(AimToleranceDegrees * MathF.PI / 180f);

    private readonly WeaponSystem weapons;
    private readonly ChunkMap terrain;

    public CombatBehavior(WeaponSystem weapons, ChunkMap terrain)
    {
        this.weapons = weapons;
        this.terrain = terrain;
    }

    /// <returns>True while combat owns movement and actor state for this tick.</returns>
    public bool Tick(
        ServerPlayer mob,
        MobBrain brain,
        uint tick,
        float dt,
        bool mayFire = true)
    {
        if (!brain.Contacts.TryNearest(mob.Position, tick, out var contact)
            || tick - contact.LastSeenTick > LostContactHoldTicks)
        {
            brain.ClearCombatTarget();
            return false;
        }

        if (brain.CombatTargetId != contact.ActorId)
        {
            brain.CombatTargetId = contact.ActorId;
            brain.TargetAcquiredTick = tick;
            brain.BurstShotsRemaining = 0;
            brain.AimDirection = Facing(mob.Yaw);
        }

        mob.Hotbar = HotbarSlot.Primary;
        float eyeHeight = mob.State.HasFlag(PlayerStateFlags.Crouching)
            ? Digging.EyeHeight - PlayerMovement.CrouchEyeDrop
            : Digging.EyeHeight;
        Vector3 origin = mob.Position + Vector3.UnitY * eyeHeight;
        Vector3 target = contact.Position + Vector3.UnitY * GunConfig.PlayerCenterHeight;
        if (!weapons.TryGetActiveWeapon(mob, out var weapon))
        {
            brain.ClearCombatTarget();
            return false;
        }

        var ballistics = BallisticsConfig.Require(weapon.Item.Type);
        Vector3 uncompensated = target - origin;
        float range = uncompensated.Length();
        if (range <= 1e-5f)
            return false;

        // Compensate only for projectile drop. Contact memory intentionally carries no live target
        // velocity, so this does not grant server-side omniscient leading.
        float flightSeconds = range / ballistics.ProjectileSpeed;
        target.Y += 0.5f * ProjectileMotion.Gravity * flightSeconds * flightSeconds;
        Vector3 desired = Vector3.Normalize(target - origin);
        brain.AimDirection = RotateTowards(
            brain.AimDirection,
            desired,
            AimTurnDegreesPerSecond * MathF.PI / 180f * dt);

        mob.Yaw = MathF.Atan2(brain.AimDirection.X, brain.AimDirection.Z);
        mob.Pitch = MathF.Asin(Math.Clamp(brain.AimDirection.Y, -1f, 1f));
        mob.LastIntent = Vector3.Zero;
        mob.State = PlayerStateFlags.Aiming;

        if (tick < mob.ReloadDoneTick)
        {
            mob.State = PlayerStateFlags.Reloading;
            return true;
        }
        if (weapon.Weapon.CurrentAmmo <= 0)
        {
            weapons.ApplyReload(mob, tick);
            mob.State = PlayerStateFlags.Reloading;
            return true;
        }

        bool visibleNow = contact.LastSeenTick == tick;
        bool reacted = tick - brain.TargetAcquiredTick >= ReactionTicks;
        bool precisionShot = range >= LongRangeStart;
        float aimToleranceCos = precisionShot
            ? MathF.Cos(LongRangeAimToleranceDegrees * MathF.PI / 180f)
            : AimToleranceCos;
        bool aimSettled = Vector3.Dot(brain.AimDirection, desired) >= aimToleranceCos;
        if (!visibleNow || !reacted || !aimSettled)
            return true;
        if (!mayFire)
        {
            brain.BurstShotsRemaining = 0;
            return true;
        }

        float aiAimMoa = AimMoaForRange(range);
        float moa = Spread.Combine(
            mob.Spread.TotalMoa(mob.State, ballistics),
            aiAimMoa);
        float probability = HitEstimate.Probability(
            Spread.SigmaRadians(moa),
            range,
            GunConfig.HitRadius);
        brain.ShouldCloseDistance = ShouldAdvance(probability, range);

        bool requestShot;
        if (precisionShot)
        {
            brain.BurstShotsRemaining = 0;
            if (tick < brain.NextPrecisionShotTick)
                return true;
            requestShot = true;
        }
        else if (probability >= AimedFireThreshold)
        {
            brain.BurstShotsRemaining = 0;
            requestShot = true;
        }
        else
        {
            if (brain.BurstShotsRemaining == 0)
            {
                if (tick < brain.NextBurstTick) return true;
                brain.BurstShotsRemaining = SuppressionBurstShots;
            }
            requestShot = true;
        }

        float shotDistance = Vector3.Distance(origin, target);
        var obstruction = TerrainRaycast.Cast(
            terrain,
            origin,
            brain.AimDirection,
            shotDistance);
        if (obstruction is { } wall && wall.Distance < shotDistance - 0.1f)
            return true;

        if (requestShot
            && weapons.TryFireAi(
                mob,
                origin,
                brain.AimDirection,
                tick,
                ++brain.ShotSequence,
                aiAimMoa))
        {
            mob.State |= PlayerStateFlags.Shooting;
            if (precisionShot)
                brain.NextPrecisionShotTick = tick + PrecisionShotIntervalTicks;
            else if (brain.BurstShotsRemaining > 0
                && --brain.BurstShotsRemaining == 0)
                brain.NextBurstTick = tick + BurstPauseTicks;
        }
        return true;
    }

    internal static bool ShouldAdvance(float hitProbability, float range)
        => float.IsFinite(hitProbability)
           && float.IsFinite(range)
           && hitProbability < AimedFireThreshold
           && range > PreferredEngagementRange;

    internal static float AimMoaForRange(float range)
    {
        float amount = Math.Clamp(
            (range - LongRangeStart)
            / (LongRangeFullAccuracy - LongRangeStart),
            0f,
            1f);
        return float.Lerp(AiAimMoa, LongRangeAimMoa, amount);
    }

    private static Vector3 Facing(float yaw)
        => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));

    private static Vector3 RotateTowards(Vector3 current, Vector3 target, float maximumRadians)
    {
        if (current.LengthSquared() < 1e-8f) return target;
        current = Vector3.Normalize(current);
        float dot = Math.Clamp(Vector3.Dot(current, target), -1f, 1f);
        float angle = MathF.Acos(dot);
        if (angle <= maximumRadians || angle <= 1e-6f) return target;
        float amount = maximumRadians / angle;
        return Vector3.Normalize(Vector3.Lerp(current, target, amount));
    }
}
