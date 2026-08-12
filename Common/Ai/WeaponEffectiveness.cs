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
    /// <summary>
    /// Fire discipline is a BURST LENGTH, and the length is chosen rather than written down.
    ///
    /// This used to be a ladder of rate fractions of cyclic — [1, 0.5, 0.25, 0.1] — executed as
    /// evenly spaced single shots. Two things were wrong with it. Evenly spaced fire is not how an
    /// automatic weapon kills: the rounds that do the killing are the two or three that arrive
    /// before the sights have moved, and a model with no burst in it cannot express that. And the
    /// dispersion each rung was scored with came from averaging recoil over a whole MAGAZINE, a
    /// window whose length depends on the rung being evaluated and on the weapon's capacity — so the
    /// rungs were not comparable to each other, and the DP-27's 47-round pan, the one thing that
    /// makes it a machine gun, was priced as its largest handicap (182 MOA against 66 for a
    /// five-round burst).
    ///
    /// What replaces it is one maximisation. Another round is always more damage and never free: it
    /// lands with more recoil on it than the last, and it delays the moment the shooter can look at
    /// what he did (<see cref="BurstAssessmentSeconds"/>) and fire again. Damage per second over
    /// FIRE PLUS ASSESSMENT therefore has an interior maximum, and where that maximum sits IS the
    /// burst — two rounds from a machine gun at ten metres because two rounds is a man, longer at
    /// distance where fewer of them land, and one from a bolt gun that cannot cycle faster anyway.
    /// Nobody writes down a burst timer and nobody writes down which weapons burst.
    /// </summary>
    private const int MaximumBurstRounds = 256;

    /// <summary>
    /// The pause between bursts: how long a man takes to see what his last one did.
    ///
    /// This is what prices the burst. Another round is more damage and never free, but the thing it
    /// costs is not the ballistics — it is that you fire a burst at a man and then have to LOOK, and
    /// a burst long enough to kill him twice spent that time twice.
    ///
    /// It is the same half-second the shooter already takes to react to a target appearing
    /// (<c>CombatBehavior.ReactionTicks</c> derives from this constant, so there is one number), for
    /// the same reason: it is one man's observe-and-decide latency, and pointing his weapon at a
    /// fresh problem or at the same one again does not change how long he needs to see it.
    ///
    /// A settle-to-zero rule was tried here first and is wrong. It made the pause a function of how
    /// much recoil the burst had built, which reads plausible and produces a submachine gunner who
    /// fires six rounds at a man fifteen metres away and then waits four and a half seconds — at
    /// which point his weapon deals 20 health per second at knife range and
    /// ThreatResponseTests.AThreatInsideItsOwnKillingRangeIsAlwaysWorthAnswering fails, correctly.
    /// Recoil already limits the burst through the falling value of each round in it; charging for
    /// it a second time as dead time is what broke.
    /// </summary>
    public const float BurstAssessmentSeconds = 0.55f;

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
