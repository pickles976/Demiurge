namespace Demiurge.Tests;

/// <summary>
/// Everything a weapon is worth at a range, in health points per second.
///
/// Orderings, never magnitudes: the numbers are tuning surface and will move. What must not move is
/// that an SMG dominates in a room and a bolt gun dominates across a field, and that neither fact is
/// written anywhere as a rule.
/// </summary>
public class WeaponEffectivenessTests
{
    private const float FullyExposed = 1f;

    [Fact]
    public void FiringFasterCostsAccuracyAtEveryRate()
    {
        // The property that makes rate a real choice: recoil must vary CONTINUOUSLY with rate. A
        // step model returns the same dispersion for every rate on a weapon's ladder, which silently
        // reduces rate selection to "always pick the fastest".
        var carbine = BallisticsConfig.Get("demiurge:carbine");
        int magazine = WeaponConfig.Require(ItemType.Sks).MagazineCapacity;

        float previous = -1f;
        foreach (float rate in new[] { 1f, 2.5f, 5f, 10f })
        {
            float recoil = WeaponEffectiveness.AverageRecoilMoa(carbine, magazine, rate);
            Assert.True(recoil > previous, $"recoil at {rate}/s ({recoil:0.0}) should exceed {previous:0.0}");
            Assert.True(recoil <= carbine.RecoilCapMoa);
            previous = recoil;
        }
    }

    [Fact]
    public void NotFiringMeansNoRecoil()
        => Assert.Equal(
            0f,
            WeaponEffectiveness.AverageRecoilMoa(
                BallisticsConfig.Get("demiurge:carbine"),
                magazineCapacity: 30,
                shotsPerSecond: 0f));

    [Fact]
    public void ReloadingDeratesTheSustainedRateBelowCyclic()
    {
        var ppsh = WeaponConfig.Require(ItemType.Ppsh);
        float cyclic = WeaponEffectiveness.CyclicShotsPerSecond(ppsh);
        float sustained = WeaponEffectiveness.SustainedShotsPerSecond(ppsh, cyclic);

        Assert.True(sustained < cyclic, "a magazine change has to cost something");
        Assert.True(sustained > 0f);
    }

    [Fact]
    public void RequestingLessThanCyclicIsHonoured()
    {
        var sks = WeaponConfig.Require(ItemType.Sks);
        float slow = WeaponEffectiveness.SustainedShotsPerSecond(sks, requestedShotsPerSecond: 1f);
        Assert.True(slow <= 1f);
    }

    [Fact]
    public void SubmachineGunDominatesInsideARoom()
    {
        var ppsh = Best(ItemType.Ppsh, range: 10f);
        var mosin = Best(ItemType.Mosin, range: 10f);
        Assert.True(
            ppsh.DamagePerSecond > mosin.DamagePerSecond,
            $"ppsh {ppsh.DamagePerSecond:0.0} should beat mosin {mosin.DamagePerSecond:0.0} at 10 m");
    }

    [Fact]
    public void BoltActionDominatesAcrossAField()
    {
        var ppsh = Best(ItemType.Ppsh, range: 100f);
        var mosin = Best(ItemType.Mosin, range: 100f);
        Assert.True(
            mosin.DamagePerSecond > ppsh.DamagePerSecond,
            $"mosin {mosin.DamagePerSecond:0.0} should beat ppsh {ppsh.DamagePerSecond:0.0} at 100 m");
    }

    [Fact]
    public void EffectivenessFallsOffMonotonicallyWithRange()
    {
        float previous = float.MaxValue;
        for (float range = 5f; range <= 200f; range += 5f)
        {
            float now = Best(ItemType.Sks, range).DamagePerSecond;
            Assert.True(now <= previous + 1e-3f, $"non-monotonic at {range} m");
            previous = now;
        }
    }

    [Fact]
    public void CoverReducesEffectivenessWithoutEliminatingIt()
    {
        float open = Best(ItemType.Sks, 40f, exposure: 1f).DamagePerSecond;
        float peeking = Best(ItemType.Sks, 40f, exposure: 0.2f).DamagePerSecond;

        Assert.True(peeking < open, "a target in cover must be harder to kill");
        Assert.True(peeking > 0f, "a target that can shoot back can be shot at");
    }

    [Fact]
    public void SuppressionDegradesAPrecisionWeaponMoreThanASprayer()
    {
        // 50 m, because both weapons must actually be firing for the comparison to mean anything —
        // at 100 m the PPSh no longer clears the per-round threshold and its "loss" is undefined.
        // Suppression adds 50 MOA in quadrature, which is a third of a rifle's total dispersion and
        // a rounding error next to an SMG's, so it costs precision far more than volume.
        float mosinLoss = FractionLostToSuppression(ItemType.Mosin, 50f);
        float ppshLoss = FractionLostToSuppression(ItemType.Ppsh, 50f);

        Assert.True(
            mosinLoss > ppshLoss,
            $"suppression should cost precision ({mosinLoss:P1}) more than spray ({ppshLoss:P1})");
    }

    private static float FractionLostToSuppression(ItemType weapon, float range)
    {
        float calm = Best(weapon, range).DamagePerSecond;
        Assert.True(calm > 0f, $"{weapon} does not fire at {range} m, so it cannot be suppressed");

        float suppressed = Best(weapon, range, extraMoa: BallisticsConfig.SuppressedMoa).DamagePerSecond;
        return 1f - suppressed / calm;
    }

    /// <summary>
    /// Fire discipline, restated for the burst model. It used to be asserted as a RATE that falls
    /// with range, and that property is gone on purpose — see the note below.
    ///
    /// What a burst is for is putting one man down, so at ten metres, where nearly every round
    /// lands, the only thing that can end a burst is that he is already down. The burst is therefore
    /// a property of the CARTRIDGE: two rounds of the machine gun's 50, six of the submachine gun's
    /// 18, and one from a bolt gun that cannot cycle a second before the first has landed anyway.
    /// </summary>
    [Fact]
    public void ACloseRangeBurstIsAsLongAsItTakesToPutAManDown()
    {
        foreach (var weapon in new[] { ItemType.Ppsh, ItemType.Sks, ItemType.Mosin, ItemType.Dp27 })
        {
            int damage = WeaponConfig.Require(weapon).Damage;
            int needed = (int)MathF.Ceiling(ThreatResponse.NominalHealth / (float)damage);
            int burst = Best(weapon, 10f).BurstRounds;

            Assert.InRange(burst, needed - 1, needed + 1);
        }
    }

    // FireDisciplineEmerges_SlowAtRange_FastUpClose is gone, and what it was pinning turned out not
    // to be real. It asserted that the chosen RATE falls with range, which the old model produced
    // because it scored each rung of its rate ladder with recoil averaged over a whole MAGAZINE — a
    // window whose length is itself a function of the rate being scored, so fast rungs were charged
    // for dispersion the slow ones escaped. Remove that and the value model does not produce
    // deliberate long-range fire at all: firing at twice the dispersion and eight times the rate
    // wins on expected damage per second whenever ammunition is nearly free, which under
    // MinimumExpectedDamagePerRound = 1 HP it is. The brake that remains is real and is tested
    // above and below — a burst stops when the man is down, and a weapon stops firing entirely once
    // no round returns its cost. If deliberate fire at range is wanted back it belongs in the price
    // of a round, not in a recoil-averaging window.

    [Fact]
    public void AShotNotWorthTheRoundIsNotTaken()
    {
        // Past the range where no rate returns its round's worth, the answer is not "fire slowly and
        // badly" but "do not fire". Zero dealt is what makes closing the distance win in
        // CombatValue, with no rule anywhere about when to advance.
        var hopeless = Best(ItemType.Ppsh, range: 400f);

        Assert.Equal(0f, hopeless.DamagePerSecond);
        Assert.Equal(0f, hopeless.ShotsPerSecond);
    }

    [Fact]
    public void TheThresholdIsOnExpectedDamagePerRoundNotPerSecond()
    {
        // Every rate that is fired must clear the per-round bar. This is the property that makes the
        // choice a threshold rather than a preference, and it is why "should I shoot or close?" has
        // an answer at all.
        for (float range = 5f; range <= 400f; range += 5f)
        {
            var solution = Best(ItemType.Sks, range);
            if (solution.ShotsPerSecond <= 0f) continue;

            float perRound = solution.HitProbability * WeaponConfig.Require(ItemType.Sks).Damage;
            Assert.True(
                perRound >= WeaponEffectiveness.MinimumExpectedDamagePerRound,
                $"fired at {range} m for {perRound:0.00} HP per round");
        }
    }

    /// <summary>
    /// Hitchman, ORO-T-160 (1952): 6% for marksmen and 25% for experts on a man-sized target at
    /// 310 yards. A competent-soldier default should land between them, and biased toward the low
    /// side — ThreatRanking's pruning bound is only sound if Phit never overestimates.
    /// </summary>
    [Fact]
    public void RifleHitProbabilityMatchesTheHitchmanCurveAtLongRange()
    {
        float probability = Best(ItemType.Mosin, range: 283f).HitProbability;
        Assert.InRange(probability, 0.03f, 0.25f);
    }

    private static WeaponEffectiveness.FiringSolution Best(
        ItemType weapon,
        float range,
        float exposure = FullyExposed,
        float extraMoa = 0f)
        => WeaponEffectiveness.Best(
            weapon, range, TargetExposure.Of(exposure), extraMoa, skillFactor: 1f);
}
