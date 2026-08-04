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
        var carbine = BallisticsConfig.Get(WeaponBallisticsProfile.Carbine);
        int magazine = WeaponConfig.Require(ItemType.Ak47).MagazineCapacity;

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
                BallisticsConfig.Get(WeaponBallisticsProfile.Carbine),
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
        var ak = WeaponConfig.Require(ItemType.Ak47);
        float slow = WeaponEffectiveness.SustainedShotsPerSecond(ak, requestedShotsPerSecond: 1f);
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
            float now = Best(ItemType.Ak47, range).DamagePerSecond;
            Assert.True(now <= previous + 1e-3f, $"non-monotonic at {range} m");
            previous = now;
        }
    }

    [Fact]
    public void CoverReducesEffectivenessWithoutEliminatingIt()
    {
        float open = Best(ItemType.Ak47, 40f, exposure: 1f).DamagePerSecond;
        float peeking = Best(ItemType.Ak47, 40f, exposure: 0.2f).DamagePerSecond;

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

    [Fact]
    public void FireDisciplineEmerges_SlowAtRange_FastUpClose()
    {
        float far = Best(ItemType.Ak47, 150f).ShotsPerSecond;
        float near = Best(ItemType.Ak47, 10f).ShotsPerSecond;

        Assert.True(
            far < near,
            $"chose {far:0.00}/s at 150 m and {near:0.00}/s at 10 m — rate should drop with range");
    }

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
            var solution = Best(ItemType.Ak47, range);
            if (solution.ShotsPerSecond <= 0f) continue;

            float perRound = solution.HitProbability * WeaponConfig.Require(ItemType.Ak47).Damage;
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
