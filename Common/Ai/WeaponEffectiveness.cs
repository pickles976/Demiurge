namespace Demiurge;

/// <summary>
/// Weapon value against one target at one range, in HP/s. Uses the shared hit-probability and
/// quadrature spread models; <see cref="CombatValue"/> composes results across believed enemies.
/// </summary>
public static class WeaponEffectiveness
{
    /// <summary>
    /// Maximum burst candidates. Burst length maximizes damage over fire plus assessment time, with
    /// each additional round paying recoil, time, and the target-health cap.
    /// </summary>
    private const int MaximumBurstRounds = 256;

    /// <summary>
    /// Observe-and-decide pause after each burst. CombatBehavior derives reaction time from the same
    /// value. Recoil is already priced per round and must not extend this pause.
    /// </summary>
    public const float BurstAssessmentSeconds = 0.55f;

    /// <summary>
    /// Minimum expected HP per round. This rejects wasteful rates and returns no firing solution when
    /// every rate falls below the threshold, allowing movement to win through <see cref="CombatValue"/>.
    /// </summary>
    public const float MinimumExpectedDamagePerRound = 1f;

    /// <param name="ShotsPerSecond">Average rate over the whole fire-and-settle cycle, reloads
    /// included — what the engagement actually delivers, not what the action can do.</param>
    /// <param name="BurstRounds">Rounds to send at the man before looking at what they did.</param>
    /// <param name="SettleSeconds">The pause that follows — see <see cref="BurstAssessmentSeconds"/>.</param>
    public readonly record struct FiringSolution(
        float ShotsPerSecond,
        float DispersionMoa,
        float HitProbability,
        float DamagePerSecond,
        int BurstRounds = 1,
        float SettleSeconds = 0f);

    private const float PreferredRangeStepMetres = 2f;
    private const float PreferredRangeMaximumMetres = 400f;

    /// <summary>
    /// Furthest range retaining <see cref="PreferredRangeFractionOfPeak"/> of peak damage. Sampled from
    /// <see cref="Best"/> because its discrete rate choice makes the curve piecewise.
    /// </summary>
    public static float PreferredRange(ItemType weapon, float skillFactor)
    {
        // Sampling calls Best 200 times. Cache by weapon and skill tenth; finer skill differences do
        // not justify distinct ranges and would create one entry per actor.
        var key = (weapon, (int)MathF.Round(Math.Clamp(skillFactor, 0.01f, 10f) * 10f));
        if (preferredRanges.TryGetValue(key, out float cached)) return cached;

        float computed = ComputePreferredRange(weapon, key.Item2 * 0.1f);
        preferredRanges[key] = computed;
        return computed;
    }

    /// <summary>Pure and deterministic, so a stale entry is impossible and no invalidation is needed:
    /// the weapon table is fixed once the datapack registry resolves.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ItemType, int), float>
        preferredRanges = new();

    private static float ComputePreferredRange(ItemType weapon, float skillFactor)
    {
        float peak = 0f;
        for (float range = PreferredRangeStepMetres;
             range <= PreferredRangeMaximumMetres;
             range += PreferredRangeStepMetres)
            peak = MathF.Max(
                peak,
                Best(
                    weapon,
                    range,
                    TargetExposure.Full,
                    extraMoa: 0f,
                    skillFactor,
                    targetHealth: float.PositiveInfinity).DamagePerSecond);

        if (peak <= 0f) return PreferredRangeStepMetres;

        // The furthest range still worth most of what the weapon can do. Taking the peak itself
        // would answer "two metres" for every weapon in the game, since closer is always better for
        // hit probability; what distinguishes them is how far out they STAY good.
        float best = PreferredRangeStepMetres;
        for (float range = PreferredRangeStepMetres;
             range <= PreferredRangeMaximumMetres;
             range += PreferredRangeStepMetres)
            if (Best(
                    weapon,
                    range,
                    TargetExposure.Full,
                    extraMoa: 0f,
                    skillFactor,
                    targetHealth: float.PositiveInfinity).DamagePerSecond
                >= PreferredRangeFractionOfPeak * peak)
                best = range;

        return best;
    }

    /// <summary>How much of its best a weapon must still deliver for a range to count as one it
    /// wants to fight at. Half: comfortably inside the useful band without reaching the tail where
    /// the shot stops paying for itself at all.</summary>
    public const float PreferredRangeFractionOfPeak = 0.5f;

    /// <summary>Cyclic rate: the fastest the action will run, ignoring reloads.</summary>
    public static float CyclicShotsPerSecond(in WeaponStats weapon)
        => weapon.TicksPerShot <= 0f
            ? 0f
            : NetworkConfig.TickRate / weapon.TicksPerShot;

    /// <summary>
    /// The rate actually achievable over a long engagement, once magazine changes are paid for.
    /// A 35-round magazine at 20 rounds/second is 1.75 s of fire against a 1.5 s reload.
    /// </summary>
    public static float SustainedShotsPerSecond(in WeaponStats weapon, float requestedShotsPerSecond)
    {
        float rate = MathF.Min(requestedShotsPerSecond, CyclicShotsPerSecond(weapon));
        if (rate <= 0f || weapon.MagazineCapacity <= 0) return MathF.Max(rate, 0f);

        float firingSeconds = weapon.MagazineCapacity / rate;
        float reloadSeconds = weapon.ReloadTicks / (float)NetworkConfig.TickRate;
        return rate * firingSeconds / (firingSeconds + reloadSeconds);
    }

    /// <summary>
    /// Average recoil dispersion over one magazine fired at <paramref name="shotsPerSecond"/>.
    ///
    /// Recoil rises by RecoilPerShotMoa per shot and decays at RecoilDecayMoaPerSecond between them,
    /// clamped to RecoilCapMoa — exactly the model <see cref="Spread.WeaponSpreadState"/> runs at
    /// execution time, so what scoring predicts is what shooting delivers.
    ///
    /// It is walked shot by shot rather than solved, because the closed form is a step function: the
    /// accumulation either outruns the decay and pins at the cap, or it does not and collapses to a
    /// single kick. That step gives IDENTICAL dispersion across every rate on a weapon's ladder —
    /// a carbine's rates all sit above its threshold and a bolt gun's all sit below — which made
    /// rate selection meaningless. The ramp through the magazine is what actually varies with rate.
    ///
    /// A magazine is at most a few dozen rounds and this runs once per candidate rate, so the loop
    /// is cheaper than the raycast it feeds.
    /// </summary>
    public static float AverageRecoilMoa(
        in BallisticsStats ballistics,
        int magazineCapacity,
        float shotsPerSecond)
    {
        if (shotsPerSecond <= 0f || ballistics.RecoilPerShotMoa <= 0f) return 0f;
        if (ballistics.RecoilDecayMoaPerSecond <= 0f) return ballistics.RecoilCapMoa;

        int shots = Math.Clamp(magazineCapacity, 1, 256);
        float decayPerShot = ballistics.RecoilDecayMoaPerSecond / shotsPerSecond;

        float recoil = 0f;
        float total = 0f;
        for (int shot = 0; shot < shots; shot++)
        {
            recoil = MathF.Min(
                ballistics.RecoilCapMoa,
                MathF.Max(0f, recoil - decayPerShot) + ballistics.RecoilPerShotMoa);
            total += recoil;
        }

        return total / shots;
    }

    /// <summary>
    /// The best firing solution against a target at <paramref name="range"/>.
    ///
    /// <paramref name="targetExposure"/> is the fraction of the target's silhouette that can be
    /// reached — 1 in the open, less behind cover. It scales the target RADIUS by its square root,
    /// because exposure is an area fraction and <see cref="HitEstimate"/> takes a radius.
    ///
    /// <paramref name="extraMoa"/> carries anything the caller already knows about the shooter's
    /// state — suppression, stance, movement — combined in quadrature like every other error source.
    ///
    /// <paramref name="skillFactor"/> scales the sighting term only. 1 is a competent soldier; above
    /// 1 is worse. It never touches decision quality, only execution.
    /// </summary>
    public static FiringSolution Best(
        ItemType weapon,
        float range,
        TargetExposure targetExposure,
        float extraMoa,
        float skillFactor,
        float targetHealth = ThreatResponse.NominalHealth)
    {
        if (WeaponConfig.Get(weapon) is not { } stats
            || BallisticsConfig.Get(weapon) is not { } ballistics
            || stats.Damage == 0)
            return default;

        float exposure = targetExposure.Fraction;
        if (exposure <= 0f) return default;

        float targetRadius = GunConfig.HitRadius * MathF.Sqrt(exposure);
        float cyclic = CyclicShotsPerSecond(stats);
        if (cyclic <= 0f) return default;

        // Everything that does not move while the burst runs: the weapon's inherent group, what the
        // shooter can hold, and whatever the caller knows about his state.
        float steadyMoa = Spread.Combine(
            ballistics.BenchMoa,
            ballistics.SightingMoa * MathF.Max(skillFactor, 0.01f),
            extraMoa);

        float decayPerShot = ballistics.RecoilDecayMoaPerSecond > 0f
            ? ballistics.RecoilDecayMoaPerSecond / cyclic
            : 0f;
        // A burst cannot outrun the magazine — past it the pause is a reload, which is a different
        // thing and already priced by SustainedShotsPerSecond.
        int limit = Math.Clamp(stats.MagazineCapacity, 1, MaximumBurstRounds);

        float recoil = 0f;
        float hits = 0f;
        float moaSum = 0f;
        var best = default(FiringSolution);
        float bestNet = 0f;

        for (int rounds = 1; rounds <= limit; rounds++)
        {
            // This round, fired with whatever the previous ones left on the sights. The first one is
            // free: a burst starts from a settled weapon, which is what the settle below pays for.
            float moa = Spread.Combine(steadyMoa, recoil);
            hits += HitEstimate.Probability(Spread.SigmaRadians(moa), range, targetRadius);
            moaSum += moa;
            recoil = MathF.Min(
                ballistics.RecoilCapMoa,
                MathF.Max(0f, recoil - decayPerShot) + ballistics.RecoilPerShotMoa);

            // A burst is aimed at a MAN, and a man only has so much in him. Without this the
            // arithmetic says to hold the trigger down at close range and mean it: every round hits,
            // so the 48th round into a corpse still improves damage per second. Capping the burst's
            // yield at one man's worth of health is what makes the answer two rounds from a machine
            // gun at ten metres.
            float dealt = MathF.Min(hits * stats.Damage, MathF.Max(1f, targetHealth));

            float cycleSeconds = rounds / cyclic + BurstAssessmentSeconds;
            float rate = SustainedShotsPerSecond(stats, rounds / cycleSeconds);
            float reloadDerate = cycleSeconds * rate / rounds;

            // Bursts are chosen on NET value and reported on GROSS damage. Net decides whether the
            // rounds are worth spending; gross is what actually lands, and CombatValue's currency
            // has to stay literal health per second or it stops being comparable to anything else.
            float net = (dealt - rounds * MinimumExpectedDamagePerRound)
                * reloadDerate / cycleSeconds;
            if (net > bestNet)
            {
                bestNet = net;
                best = new FiringSolution(
                    rate,
                    moaSum / rounds,
                    hits / rounds,
                    dealt * reloadDerate / cycleSeconds,
                    rounds,
                    BurstAssessmentSeconds);
            }

            // Past a killing burst every further round costs time and buys nothing, so the maximum
            // is behind us.
            if (dealt >= targetHealth) break;
        }

        // No burst returned its rounds' worth: the honest answer is that shooting from here is not
        // worth doing. Zero dealt is what makes closing the distance win, rather than a rule saying
        // so.
        return bestNet > 0f ? best : default;
    }
}
