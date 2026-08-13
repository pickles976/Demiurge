using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// First combat layer: hold position, settle aim, and request authoritative rifle fire against
/// believed contacts. Movement-under-fire and cover selection deliberately belong to later layers.
/// </summary>
internal sealed class CombatBehavior
{
    private const float AimToleranceDegrees = 7f;
    /// <summary>
    /// How far the barrel sits below the eye. It matches what the PLAYER's muzzle does at the same
    /// moment: LocalPlayerController takes his origin from the weapon model's own barrel, which
    /// while aiming is about this far under his camera. The two sides now shoot from comparable
    /// places rather than the AI shooting from its own eye.
    ///
    /// Vertical only, deliberately. A forward offset along the barrel would be more faithful still,
    /// but it depends on the aim direction that is computed FROM this origin, and it can push the
    /// muzzle out of the man and into whatever he is standing behind — which would make him
    /// permanently mute rather than merely lower.
    /// </summary>
    private const float MuzzleDropFromEye = 0.22f;

    private const float AimTurnDegreesPerSecond = 180f;
    private const int ReactionTicks = (55 * NetworkConfig.TickRate + 99) / 100;
    private const int LostContactHoldTicks = 3 * NetworkConfig.TickRate / 2;

    /// <summary>
    /// Beyond this the shot is treated as deliberate: the actor must have its aim settled far more
    /// tightly before it will pull, and it fires on the slower precision cadence.
    ///
    /// This is aim DISCIPLINE, not an engagement ceiling — it says how carefully to shoot, never
    /// whether the weapon can reach. It survived the removal of the ItemType range table for that
    /// reason, and it is deliberately weapon-independent: settling the sights is the shooter's job.
    /// </summary>
    private const float DeliberateShotRange = 30f;
    private const float DeliberateAimToleranceDegrees = 2.5f;

    // Gone, and deliberately not replaced:
    //
    //   AiAimMoa 720, LongRangeAimMoa 180, LongRangeStart, LongRangeFullAccuracy
    //     A flat aim error that swamped every weapon's own dispersion — Spread.Combine(720, 4) =
    //     720.01 — so an NPC shot every gun identically. Now BallisticsStats.SightingMoa scaled by
    //     the actor's skill.
    //
    //   PpshEffectiveRange 75, DefaultMaxEngagementRange 70, SksMaxEngagementRange 100,
    //   MosinMaxEngagementRange 150, PreferredEngagementRange 25
    //     Engagement ceilings keyed on ItemType, which existed only because the flat aim term had
    //     erased the difference the ballistics table already described. A ceiling still exists; it
    //     is now the range at which a round stops being worth its expected return
    //     (WeaponEffectiveness.MinimumExpectedDamagePerRound).

    /// <summary>
    /// Suppressing fire is aimed at a place rather than a visible body, so it is deliberately slower
    /// than aimed fire and does not wait for the target to reappear. Its purpose is the suppression the
    /// weapon system already applies on a near miss, which is what lets a squadmate move.
    /// </summary>
    private const int SuppressionShotIntervalTicks = 2 * NetworkConfig.TickRate / 3;
    private const int SuppressionMemoryTicks = 4 * NetworkConfig.TickRate;
    private static readonly float AimToleranceCos =
        MathF.Cos(AimToleranceDegrees * MathF.PI / 180f);

    private readonly WeaponSystem weapons;
    private readonly ChunkMap terrain;

    public CombatBehavior(WeaponSystem weapons, ChunkMap terrain)
    {
        this.weapons = weapons;
        this.terrain = terrain;
    }

    /// <returns>What the actor should look like, and whether combat owns its movement this tick.
    /// It does NOT write any of that to <paramref name="mob"/> — see <see cref="CombatOutcome"/>.</returns>
    public CombatOutcome Tick(
        ServerPlayer mob,
        MobBrain brain,
        uint tick,
        float dt,
        bool mayFire = true,
        bool suppressing = false)
    {
        // Suppression keeps a contact alive far longer than aimed fire does: the whole point is to keep
        // shooting at where he is while he has his head down and is therefore not visible.
        uint holdTicks = (uint)(suppressing ? SuppressionMemoryTicks : LostContactHoldTicks);
        if (!TryHighestThreat(mob, brain, tick, out var contact)
            || tick - contact.LastSeenTick > holdTicks)
        {
            brain.ClearCombatTarget();
            return CombatOutcome.None;
        }

        // Left as a write: selecting the primary is an inventory action, not a description of the
        // actor, and MobSystem sets the same thing a few lines later.
        mob.Hotbar = HotbarSlot.Primary;
        // No engagement-range table. A weapon whose expected return per round falls below
        // WeaponEffectiveness.MinimumExpectedDamagePerRound yields a zero firing solution, and that
        // IS the decision not to engage — derived from dispersion, damage and cadence rather than
        // from three hand-set constants keyed on ItemType.
        if (!weapons.TryGetActiveWeapon(mob, out var weapon)
            || WeaponEffectiveness.Best(
                weapon.Item.Type,
                HorizontalDistance(mob.Position, contact.Position),
                TargetExposure.Full,
                extraMoa: 0f,
                brain.SkillFactor).DamagePerSecond <= 0f)
        {
            brain.ClearCombatTarget();
            return CombatOutcome.None;
        }

        var flags = PlayerStateFlags.None;

        if (brain.CombatTargetId != contact.ActorId)
        {
            brain.CombatTargetId = contact.ActorId;
            brain.TargetAcquiredTick = tick;
            brain.BurstShotsRemaining = 0;
            brain.AimDirection = Facing(mob.Yaw);
        }

        // A man shoots from his weapon, not from his eye. Perception still works off the eye — that
        // is where seeing happens — but the round leaves the barrel, which sits below it, and the
        // difference decides every shot taken over a crest or a parapet. Firing from the eye made a
        // visible target a shootable one BY CONSTRUCTION, which is the shape of an unfair advantage
        // even when it produced no complaint.
        float eyeHeight = mob.State.EyeHeight();
        Vector3 origin = mob.Position + Vector3.UnitY * (eyeHeight - MuzzleDropFromEye);
        // Aim at whatever part of him perception actually had a line to. Centre mass is the fallback
        // for a remembered contact: nothing was seen this tick, and the obstruction check below
        // refuses the shot anyway.
        float aimHeight = brain.PerceivedTargetId == contact.ActorId
            ? brain.PerceivedAimHeight
            : GunConfig.PlayerCenterHeight;
        Vector3 target = contact.Position + Vector3.UnitY * aimHeight;
        var ballistics = BallisticsConfig.Require(weapon.Item.Type);
        Vector3 uncompensated = target - origin;
        float range = uncompensated.Length();
        if (range <= 1e-5f)
            return CombatOutcome.None;
        // Whether this shot is worth its round, asked of the weapon rather than of its identity.
        // PrefersToHoldFire tested `weapon == ItemType.Ppsh && range > 75`, so only the SMG ever
        // decided to close — a rifleman past its own useful range went mute and stood there, because
        // nothing set ShouldCloseDistance for him.
        //
        // Exposure is deliberately 1 here. This is a question about the weapon's REACH, not about
        // the target's cover: a man behind a parapet should be shot at less eagerly, but he should
        // not make a rifleman conclude his rifle has stopped working.
        bool holdingForEffectiveRange = WeaponEffectiveness.Best(
            weapon.Item.Type,
            range,
            TargetExposure.Full,
            extraMoa: 0f,
            brain.SkillFactor).DamagePerSecond <= 0f;
        brain.ShouldCloseDistance = holdingForEffectiveRange;

        // How to shoot, as opposed to whether the weapon reaches, is asked WITH the shooter's own
        // state in it — his stance, whether he is walking, and what is landing near him — so the
        // burst he chooses is scored the way the shot will actually be taken. Spread.StateMoa is
        // precisely the terms Best does not model for itself.
        //
        // Deliberately NOT fed into the reach test above. Suppression adds 145 MOA, which is enough
        // to zero a firing solution outright, and a zero solution IS the decision to close — so a
        // man being shot at would conclude his rifle had stopped working and charge the gun that was
        // suppressing him. Being suppressed does not shorten a weapon's reach; it spoils the shot,
        // which is what this is for.
        var solution = WeaponEffectiveness.Best(
            weapon.Item.Type,
            range,
            TargetExposure.Full,
            mob.Spread.StateMoa(mob.State),
            brain.SkillFactor);

        // Compensate only for projectile drop. Contact memory intentionally carries no live target
        // velocity, so this does not grant server-side omniscient leading.
        float flightSeconds = range / ballistics.ProjectileSpeed;
        target.Y += 0.5f * ProjectileMotion.Gravity * flightSeconds * flightSeconds;
        Vector3 desired = Vector3.Normalize(target - origin);
        brain.AimDirection = RotateTowards(
            brain.AimDirection,
            desired,
            AimTurnDegreesPerSecond * MathF.PI / 180f * dt);

        float yaw = MathF.Atan2(brain.AimDirection.X, brain.AimDirection.Z);
        float pitch = MathF.Asin(Math.Clamp(brain.AimDirection.Y, -1f, 1f));
        flags = PlayerStateFlags.Aiming;

        if (tick < mob.ReloadDoneTick)
        {
            flags = PlayerStateFlags.Reloading;
            return new CombatOutcome(true, yaw, pitch, flags);
        }
        if (weapon.Weapon.CurrentAmmo <= 0)
        {
            weapons.ApplyReload(mob, tick);
            flags = PlayerStateFlags.Reloading;
            return new CombatOutcome(true, yaw, pitch, flags);
        }

        bool visibleNow = contact.LastSeenTick == tick;
        bool reacted = tick - brain.TargetAcquiredTick >= ReactionTicks;
        // A suppressing gunner shoots at the last known position. Requiring current visibility made
        // suppression impossible against exactly the target it exists for: one that is behind cover.
        if (suppressing) visibleNow = true;
        bool precisionShot = range >= DeliberateShotRange;
        float aimToleranceCos = precisionShot
            ? MathF.Cos(DeliberateAimToleranceDegrees * MathF.PI / 180f)
            : AimToleranceCos;
        bool aimSettled = Vector3.Dot(brain.AimDirection, desired) >= aimToleranceCos;
        if (!visibleNow || !reacted || !aimSettled)
            return new CombatOutcome(true, yaw, pitch, flags);
        if (!mayFire || holdingForEffectiveRange)
        {
            brain.BurstShotsRemaining = 0;
            return new CombatOutcome(true, yaw, pitch, flags);
        }

        // The shooter's aim error is now a property of the weapon he is holding
        // (BallisticsStats.SightingMoa) scaled by his own skill, not a flat 720 MOA constant that
        // swamped every weapon's dispersion — Spread.Combine(720, 4) = 720.01, which is why weapon
        // character previously had to be reintroduced by hand as ItemType branches.
        float aiAimMoa = ballistics.SightingMoa * MathF.Max(brain.SkillFactor, 0.01f);

        bool requestShot;
        if (suppressing)
        {
            // Suppression is not killing and is deliberately not on the burst cadence. Its product is
            // rounds landing near a man CONTINUOUSLY so a squadmate can move, and a burst-and-settle
            // pattern would hand him the gap.
            brain.BurstShotsRemaining = 0;
            if (tick < brain.NextSuppressionShotTick) return new CombatOutcome(true, yaw, pitch, flags);
            brain.NextSuppressionShotTick = tick + SuppressionShotIntervalTicks;
            requestShot = true;
        }
        else
        {
            // Fire the burst the firing solution asked for, then let the sights come back down for
            // as long as it said. Both numbers are derived per weapon per range — see
            // WeaponEffectiveness — so a machine gun sends two rounds at ten metres and three at a
            // hundred, and a bolt gun sends one, without any of those being written down here.
            //
            // The burst is latched when it STARTS. A target that steps behind cover mid-burst does
            // not retroactively change how many rounds were worth sending, and re-solving every tick
            // would let a shot-to-shot flicker in range restart the cadence forever.
            if (brain.BurstShotsRemaining <= 0)
            {
                if (tick < brain.NextBurstTick) return new CombatOutcome(true, yaw, pitch, flags);
                brain.BurstShotsRemaining = Math.Max(1, solution.BurstRounds);
                brain.BurstSettleTicks = (uint)MathF.Max(
                    1f,
                    MathF.Round(solution.SettleSeconds * NetworkConfig.TickRate));
            }
            requestShot = true;
        }

        // Asked of the weapon system, which is what will resolve the round: terrain AND the things
        // standing in it. Casting terrain here on its own is how NPCs came to fire into tree trunks.
        float shotDistance = Vector3.Distance(origin, target);
        if (weapons.IsShotBlocked(origin, brain.AimDirection, shotDistance))
            return new CombatOutcome(true, yaw, pitch, flags);

        if (requestShot
            && weapons.TryFireAi(
                mob,
                origin,
                brain.AimDirection,
                tick,
                ++brain.ShotSequence,
                aiAimMoa))
        {
            flags |= PlayerStateFlags.Shooting;
            if (brain.BurstShotsRemaining > 0 && --brain.BurstShotsRemaining == 0)
                brain.NextBurstTick = tick + brain.BurstSettleTicks;
        }
        return new CombatOutcome(true, yaw, pitch, flags);
    }





    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static bool TryHighestThreat(
        ServerPlayer mob,
        MobBrain brain,
        uint tick,
        out AiContact contact)
    {
        var contacts = brain.Contacts.Snapshot(tick);
        contact = default;
        if (contacts.Count == 0) return false;

        var engagements = new Engagement[contacts.Count];
        for (int i = 0; i < contacts.Count; i++)
        {
            var candidate = contacts[i];
            engagements[i] = new Engagement(
                HorizontalDistance(mob.Position, candidate.Position),
                candidate.ObservedWeapon ?? ItemConfig.UnidentifiedThreatWeapon,
                candidate.ObservedExtraMoa,
                TargetExposure.Full,
                brain.SelfExposure,
                candidate.TargetingLikelihood);
        }

        Span<ThreatBound> ranked = stackalloc ThreatBound[1];
        if (ThreatRanking.Rank(engagements, ranked) == 0) return false;
        contact = contacts[ranked[0].Index];
        return true;
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
