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
