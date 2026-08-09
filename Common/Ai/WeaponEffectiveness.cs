namespace Demiurge;

/// <summary>
/// What a weapon is worth against one target at one range, in health points per second.
///
/// This is the half of the combat currency that depends only on the weapon and the geometry — no
/// squad, no objective, no terrain. <see cref="CombatValue"/> composes it over believed enemies.
///
/// Nothing here invents a range curve. <see cref="HitEstimate.Probability"/> is already the standard
/// dispersion model (Rayleigh CDF over a bivariate-normal aim error against a circular target), and
/// <see cref="Spread"/> already sums error sources in quadrature the way independent errors add. The
/// only thing that was missing is that the AI drowned all of it in a flat 720 MOA aim constant —
/// see BallisticsStats.SightingMoa.
/// </summary>
public static class WeaponEffectiveness
{
    /// <summary>Rates considered when choosing how fast to shoot. Fire discipline is the choice
    /// between these, not a burst timer: at range the slow entries win because dispersion is a
    /// function of rate, and up close the fast ones win because volume is.</summary>
    private static readonly float[] RateFractions = [1f, 0.5f, 0.25f, 0.1f];

    /// <summary>
    /// What one round has to be expected to achieve before it is worth firing, in health points.
    ///
    /// Without it, maximising damage per second always picks the fastest rate available, at every
    /// range, for every weapon — and that is not a tuning artefact but arithmetic: rate enters the
    /// product linearly while hit probability enters sub-linearly, so halving the rate always halves
    /// the damage and never doubles the chance. Measured on the AK at 150 m, dropping from 6.67 to
    /// 0.95 rounds/second bought 1.44x the hit probability and cost 7x the volume.
    ///
    /// A per-round cost fixes it, and does so more sharply than a weighting would. Because rate
    /// multiplies the whole expression —
    ///
    ///     rate * (Phit * damage - MinimumExpectedDamagePerRound)
    ///
    /// — this is a THRESHOLD on expected damage per round, not a graduated preference. Rates whose
    /// expected return per round falls below it are rejected outright, and the fastest surviving rate
    /// wins. So a rifleman sprays a man at ten metres, fires deliberately at one at a hundred and
    /// fifty, and declines the shot entirely past the range where no rate pays — at which point
    /// dealt is zero and closing the distance wins in <see cref="CombatValue"/> without anybody
    /// writing a rule about when to advance.
    ///
    /// One health point is a 3.3% hit chance for a 30-damage carbine, which is roughly where a real
    /// soldier stops shooting and starts moving.
    /// </summary>
    public const float MinimumExpectedDamagePerRound = 1f;

    public readonly record struct FiringSolution(
        float ShotsPerSecond,
        float DispersionMoa,
        float HitProbability,
        float DamagePerSecond);

    private const float PreferredRangeStepMetres = 2f;
    private const float PreferredRangeMaximumMetres = 400f;

    /// <summary>
    /// The range at which this weapon is worth the most, in metres.
    ///
    /// Sampled off the same curve everything else uses rather than written down per weapon, which is
    /// the point: "the SMG man closes and the rifleman holds" stops being doctrine anybody
    /// implements and becomes where two numbers peak. Damage per second is rate times hit
    /// probability, rate is flat in range and hit probability falls — so the maximum would sit at the
    /// sampling floor for everything, were it not for the rate CHOICE inside <see cref="Best"/>:
    /// <see cref="MinimumExpectedDamagePerRound"/> forces a slower, tighter rate as range grows, and
    /// how gracefully a weapon makes that trade is exactly what separates a submachine gun from a
    /// bolt gun. Hence the fraction-of-peak form below rather than the peak itself.
    ///
    /// Sampled rather than solved because that rate choice is discrete, so the curve is piecewise
    /// and has no closed form worth deriving. Called per squad plan at 2 Hz, not per actor per tick.
    /// </summary>
    public static float PreferredRange(ItemType weapon, float skillFactor)
    {
        // Memoized because the sampling is 400 calls into Best() and the callers are hot: MobSystem
        // asks per actor per tick, which at 32 NPCs and 30 Hz would be hundreds of thousands of
        // firing solutions a second for an answer that only changes when the weapon table does.
        // Measured before caching: the server test project went from 0.5 s to 5 s.
        //
        // Skill is quantized into tenths because it scales the sighting term smoothly — two men a
        // hundredth apart do not want to fight at different ranges, and an unquantized key would
        // make the cache a memory leak with one entry per actor.
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
                Best(weapon, range, TargetExposure.Full, extraMoa: 0f, skillFactor).DamagePerSecond);

        if (peak <= 0f) return PreferredRangeStepMetres;

        // The furthest range still worth most of what the weapon can do. Taking the peak itself
        // would answer "two metres" for every weapon in the game, since closer is always better for
        // hit probability; what distinguishes them is how far out they STAY good.
        float best = PreferredRangeStepMetres;
        for (float range = PreferredRangeStepMetres;
             range <= PreferredRangeMaximumMetres;
             range += PreferredRangeStepMetres)
            if (Best(weapon, range, TargetExposure.Full, extraMoa: 0f, skillFactor).DamagePerSecond
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
        float skillFactor)
    {
        if (WeaponConfig.Get(weapon) is not { } stats
            || BallisticsConfig.Get(weapon) is not { } ballistics
            || stats.Damage == 0)
            return default;

        float exposure = targetExposure.Fraction;
        if (exposure <= 0f) return default;

        float targetRadius = GunConfig.HitRadius * MathF.Sqrt(exposure);
        float cyclic = CyclicShotsPerSecond(stats);
        var best = default(FiringSolution);
        float bestNet = 0f;

        foreach (float fraction in RateFractions)
        {
            float requested = cyclic * fraction;
            float rate = SustainedShotsPerSecond(stats, requested);
            if (rate <= 0f) continue;

            // Recoil depends on the rate being requested, not the reload-derated average: the
            // shooter feels the cyclic rate while the magazine lasts.
            float moa = Spread.Combine(
                ballistics.BenchMoa,
                ballistics.SightingMoa * MathF.Max(skillFactor, 0.01f),
                AverageRecoilMoa(ballistics, stats.MagazineCapacity, requested),
                extraMoa);

            float probability = HitEstimate.Probability(
                Spread.SigmaRadians(moa),
                range,
                targetRadius);

            // Rates are chosen on NET value and reported on GROSS damage. Net decides whether a shot
            // is worth the round; gross is what actually lands, and CombatValue's currency has to
            // stay literal health per second or it stops being comparable to anything else.
            float expectedPerRound = probability * stats.Damage;
            float netPerSecond = (expectedPerRound - MinimumExpectedDamagePerRound) * rate;
            if (netPerSecond <= bestNet) continue;

            bestNet = netPerSecond;
            best = new FiringSolution(rate, moa, probability, expectedPerRound * rate);
        }

        // No rate returned its round's worth: the honest answer is that shooting from here is not
        // worth doing. Zero dealt is what makes closing the distance win, rather than a rule saying
        // so.
        return bestNet > 0f ? best : default;
    }
}
