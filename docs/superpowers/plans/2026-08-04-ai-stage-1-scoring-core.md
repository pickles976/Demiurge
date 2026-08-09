# NPC AI Stage 1 — Scoring Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **STATUS, 2026-08-09 — this plan was executed without being ticked. Every checkbox below is still
> `- [ ]` and most of the work is in the codebase, so THIS FILE IS NOT A STATUS SOURCE.** Read the
> code and "The combat currency" in `docs/ARCHITECTURE.md` instead.
>
> Landed: `Common/Ai/{CombatValue,WeaponEffectiveness,ThreatRanking,Exposure}.cs`; `SightingMoa`
> replacing the flat `AiAimMoa`; rate-as-a-choice; `MaxEngagementRangeFor`, `PrefersToHoldFire` and
> `ShouldAdvance` deleted; `SquadTactics` joint allocation with the suppression externality; the
> cover budget opened from 1/tick to 8.
>
> **Not landed — the goal sentence immediately below, and the reason this file is kept:**
> `MobSystem`'s per-unit arbitration is still an ordered `ActorIntent` ternary, and
> `ActorIntent.HoldAndFire` is declared and never constructed. That is the remaining stage-1 work.

**Goal:** Replace `MobSystem`'s per-unit arbitration and `SquadTactics`' doctrine with one currency — net HP/second — so that role, range discipline, fire discipline and the decision to dig all fall out of a single score instead of `ItemType` branches.

**Architecture:** All scoring maths is pure and lives in `Common/Ai/`, so it tests headlessly without booting Stride or a server. The server layer supplies observations and consumes scores. Hit probability is *not* invented: `Common/Ballistics/HitEstimate.Probability` already implements the standard Rayleigh dispersion model, and this plan makes it visible to the AI by replacing a flat `AiAimMoa = 720` constant (which arithmetically annihilated per-weapon dispersion) with a per-profile `SightingMoa`.

**Tech Stack:** C# / net10.0, xUnit, `System.Numerics`. No new dependencies.

## Global Constraints

- **Never run `git commit`.** Sebastian reviews and commits. Every task ends with a build-and-test verification step instead of a commit step.
- `Common` must not reference Stride. Anything placed in `Common/Ai/` must compile and test without the engine.
- `DemiurgeSharp.csproj` at the repo root globs `**/*.cs`; every sibling project needs a `<Compile Remove="Dir/**/*.cs" />`. Adding files inside existing project directories needs no csproj change.
- Enum values and wire identity are append-only. `WeaponStats`/`BallisticsStats` are explicitly **not** on the wire (see the comment on `WeaponStats`), so adding fields to them is safe.
- Test commands: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~<Name>"` and `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~<Name>"`.
- Namespaces: `Demiurge` for `Common/`, `Demiurge.GameServer` for `Server/`, `Demiurge.Tests` for `Common.Tests/`, `Demiurge.ServerTests` for `Server.Tests/`.
- `GunConfig.HitRadius` is 0.6 m. `NetworkConfig.TickRate` is 30.
- Assert **orderings and properties**, never magnitudes, so tuning can move numbers without breaking tests.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `Common/Ballistics/BallisticsConfig.cs` (modify) | Gains `SightingMoa` per profile — the weapon–shooter aiming error. |
| `Common/Ai/WeaponEffectiveness.cs` (create) | Sustained rate, steady-state recoil, duty cycle, damage-per-second at a range. One weapon, one target. |
| `Common/Ai/CombatValue.cs` (create) | The currency. `Engagement` observations in, net HP/s out. |
| `Common/Ai/ThreatRanking.cs` (create) | Exposure-free upper bound, sort, budget. Sound pruning. |
| `Common/Ai/HeardShots.cs` (create) | Salience-ranked gunshot memory, replacing `MobBrain`'s single slot. |
| `Common/Ai/GunshotHearing.cs` (modify) | Positional error scaled by distance. |
| `Server/Ai/SquadTactics.cs` (rewrite) | Allocation over member scores, including the applied-suppression externality. |
| `Server/Ai/CombatBehavior.cs` (modify) | Deletes the `ItemType` branches and hand-set cadences; fires at the rate the score chose. |
| `Server/Ai/MobBrain.cs` (modify) | Swaps the single gunshot slot for `HeardShots`; drops cover-gating state. |
| `Server/MobSystem.cs` (modify) | Wires scores into arbitration; opens the cover budget; sticky squad membership. |

---

### Task 1: Make per-weapon dispersion visible to the AI

The AI currently combines the weapon's dispersion with a flat `AiAimMoa = 720` in quadrature, so `Spread.Combine(720, 4) = 720.01` — the weapon contributes 0.04 MOA out of 720 and every weapon is identical to an NPC. This task adds the field that fixes it. Nothing consumes it yet.

**Files:**
- Modify: `Common/Ballistics/BallisticsConfig.cs`
- Test: `Common.Tests/WeaponDispersionTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `BallisticsStats.SightingMoa` (float, last positional parameter, defaulted). `BallisticsConfig.Get(WeaponBallisticsProfile)` returns it populated.

- [ ] **Step 1: Write the failing test**

Create `Common.Tests/WeaponDispersionTests.cs`:

```csharp
namespace Demiurge.Tests;

/// <summary>
/// The aiming error is a property of the weapon-shooter SYSTEM — sight radius, sight picture,
/// trigger, weight — not a constant of the shooter. A flat AI aim constant makes every weapon
/// identical, which is why weapon character had to be reintroduced as ItemType branches.
/// </summary>
public class WeaponDispersionTests
{
    [Fact]
    public void SightingErrorOrdersWeaponsFromPrecisionToSpray()
    {
        float sniper = BallisticsConfig.Get(WeaponBallisticsProfile.SniperRifle).SightingMoa;
        float semiAuto = BallisticsConfig.Get(WeaponBallisticsProfile.SemiAutomaticRifle).SightingMoa;
        float carbine = BallisticsConfig.Get(WeaponBallisticsProfile.Carbine).SightingMoa;
        float pistol = BallisticsConfig.Get(WeaponBallisticsProfile.Pistol).SightingMoa;

        Assert.True(sniper < semiAuto, "a scoped bolt gun must aim tighter than a semi-automatic rifle");
        Assert.True(semiAuto < carbine, "a marksman rifle must aim tighter than a carbine");
        Assert.True(carbine < pistol, "a carbine must aim tighter than a pistol-calibre weapon");
    }

    [Fact]
    public void SightingErrorDominatesBenchDispersionForAHumanShooter()
    {
        // The point of the field: bench dispersion (2-8 MOA) is irrelevant next to how well a
        // person can hold the sights. If these were the same order, the field would be pointless.
        var carbine = BallisticsConfig.Get(WeaponBallisticsProfile.Carbine);
        Assert.True(
            carbine.SightingMoa > carbine.BenchMoa * 10f,
            $"sighting {carbine.SightingMoa} should dwarf bench {carbine.BenchMoa}");
    }

    [Fact]
    public void ThrowableHasNoSightingError()
        => Assert.Equal(0f, BallisticsConfig.Get(WeaponBallisticsProfile.Throwable).SightingMoa);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~WeaponDispersionTests"`
Expected: FAIL to compile — `'BallisticsStats' does not contain a definition for 'SightingMoa'`.

- [ ] **Step 3: Add the field**

In `Common/Ballistics/BallisticsConfig.cs`, extend the record and every profile row:

```csharp
    /// <summary>
    /// Projectile, accuracy, and recoil characteristics shared by a weapon class.
    /// MOA values describe the diameter of the circle containing 95% of shots.
    /// </summary>
    public readonly record struct BallisticsStats(
        float ProjectileSpeed,
        float BenchMoa,
        float RecoilPerShotMoa,
        float RecoilDecayMoaPerSecond,
        float RecoilCapMoa,
        /// <summary>
        /// How well a competent shooter can hold this weapon's sights on a target, as a 95% group
        /// diameter. This is a property of the weapon-shooter SYSTEM — sight radius, sight picture,
        /// trigger weight, how steady the thing is — not of the shooter alone, which is why it lives
        /// per profile rather than as one constant.
        ///
        /// It exists because a flat AI aim term made per-weapon dispersion arithmetically invisible:
        /// Spread.Combine(720, 4) = 720.01, so BenchMoa contributed 0.04 MOA out of 720 and every
        /// weapon had identical hit probability at every range. That is why CombatBehavior needed
        /// MaxEngagementRangeFor to reintroduce weapon character by hand.
        ///
        /// Calibrated against Hitchman, ORO-T-160 (1952): a rifleman scores roughly 6% (marksman) to
        /// 25% (expert) on a man-sized target at 310 yards. The per-NPC skill dial scales this term,
        /// which is what reproduces that spread.
        /// </summary>
        float SightingMoa = 0f);
```

Then populate each row:

```csharp
            WeaponBallisticsProfile.SniperRifle => new BallisticsStats(
                ProjectileSpeed: 850f,
                BenchMoa: 2f,
                RecoilPerShotMoa: 70f,
                RecoilDecayMoaPerSecond: 70f,
                RecoilCapMoa: 70f,
                SightingMoa: 60f),
            WeaponBallisticsProfile.SemiAutomaticRifle => new BallisticsStats(
                ProjectileSpeed: 800f,
                BenchMoa: 3f,
                RecoilPerShotMoa: 32f,
                RecoilDecayMoaPerSecond: 40f,
                RecoilCapMoa: 190f,
                SightingMoa: 150f),
            WeaponBallisticsProfile.Carbine => new BallisticsStats(
                ProjectileSpeed: 715f,
                BenchMoa: 4f,
                RecoilPerShotMoa: 36f,
                RecoilDecayMoaPerSecond: 30f,
                RecoilCapMoa: 220f,
                SightingMoa: 200f),
            WeaponBallisticsProfile.Pistol => new BallisticsStats(
                ProjectileSpeed: 375f,
                BenchMoa: 8f,
                RecoilPerShotMoa: 30f,
                RecoilDecayMoaPerSecond: 35f,
                RecoilCapMoa: 150f,
                SightingMoa: 400f),
            WeaponBallisticsProfile.Throwable => new BallisticsStats(
                ProjectileSpeed: GrenadeConfig.ThrowSpeed,
                BenchMoa: 0f,
                RecoilPerShotMoa: 0f,
                RecoilDecayMoaPerSecond: 0f,
                RecoilCapMoa: 0f,
                SightingMoa: 0f),
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~WeaponDispersionTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Verify nothing else broke**

Run: `dotnet build DemiurgeSharp.slnx && dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"`
Expected: build succeeds, full fast suite passes. `SightingMoa` is defaulted, so existing construction sites are unaffected.

---

### Task 2: Weapon effectiveness — sustained rate, steady-state recoil, duty cycle

`dealt` cannot use first-shot dispersion. Recoil accumulates against decay and saturates at the cap, so a PPSh at 20 rounds/second is pinned at 150 MOA while a Mosin at 0.67/second recovers between shots. Firing rate is also a *choice*, which is what makes fire discipline emerge rather than being a burst timer.

**Files:**
- Create: `Common/Ai/WeaponEffectiveness.cs`
- Test: `Common.Tests/WeaponEffectivenessTests.cs` (create)

**Interfaces:**
- Consumes: `BallisticsStats.SightingMoa` from Task 1.
- Produces:
  - `WeaponEffectiveness.SteadyStateRecoilMoa(in BallisticsStats, float shotsPerSecond) -> float`
  - `WeaponEffectiveness.CyclicShotsPerSecond(in WeaponStats) -> float`
  - `WeaponEffectiveness.SustainedShotsPerSecond(in WeaponStats, float requestedRate) -> float`
  - `WeaponEffectiveness.FiringSolution` — `readonly record struct FiringSolution(float ShotsPerSecond, float DispersionMoa, float HitProbability, float DamagePerSecond)`
  - `WeaponEffectiveness.Best(ItemType weapon, float range, float targetExposure, float extraMoa, float skillFactor) -> FiringSolution`

- [ ] **Step 1: Write the failing test**

Create `Common.Tests/WeaponEffectivenessTests.cs`:

```csharp
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
    public void SustainedFirePinsRecoilAtTheCapButSlowFireRecovers()
    {
        var pistol = BallisticsConfig.Get(WeaponBallisticsProfile.Pistol);
        var sniper = BallisticsConfig.Get(WeaponBallisticsProfile.SniperRifle);

        // 20 rounds/second against 35 MOA/second of decay: it never catches up.
        Assert.Equal(
            pistol.RecoilCapMoa,
            WeaponEffectiveness.SteadyStateRecoilMoa(pistol, shotsPerSecond: 20f));

        // A bolt gun's cycle is longer than its recovery, so it starts each shot nearly settled.
        float bolt = WeaponEffectiveness.SteadyStateRecoilMoa(sniper, shotsPerSecond: 0.67f);
        Assert.True(bolt < sniper.RecoilCapMoa, $"bolt recoil {bolt} should recover below the cap");
    }

    [Fact]
    public void NotFiringMeansNoRecoil()
        => Assert.Equal(
            0f,
            WeaponEffectiveness.SteadyStateRecoilMoa(
                BallisticsConfig.Get(WeaponBallisticsProfile.Carbine),
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
        float mosinLoss = 1f - Best(ItemType.Mosin, 100f, extraMoa: BallisticsConfig.SuppressedMoa)
                .DamagePerSecond
            / Best(ItemType.Mosin, 100f).DamagePerSecond;
        float ppshLoss = 1f - Best(ItemType.Ppsh, 100f, extraMoa: BallisticsConfig.SuppressedMoa)
                .DamagePerSecond
            / Best(ItemType.Ppsh, 100f).DamagePerSecond;

        Assert.True(
            mosinLoss > ppshLoss,
            $"suppression should cost precision ({mosinLoss:P0}) more than spray ({ppshLoss:P0})");
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
        => WeaponEffectiveness.Best(weapon, range, exposure, extraMoa, skillFactor: 1f);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~WeaponEffectivenessTests"`
Expected: FAIL to compile — `The name 'WeaponEffectiveness' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `Common/Ai/WeaponEffectiveness.cs`:

```csharp
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
    /// Recoil dispersion once firing has settled.
    ///
    /// Recoil rises by RecoilPerShotMoa per shot and falls at RecoilDecayMoaPerSecond. Above the rate
    /// where those balance it can never catch up and saturates at the cap; below it, the shooter
    /// recovers between shots and only carries the residue of the last one. That threshold is the
    /// entire difference between an SMG and a bolt gun in sustained fire, and it is already in the
    /// ballistics table — it was simply never read.
    /// </summary>
    public static float SteadyStateRecoilMoa(in BallisticsStats ballistics, float shotsPerSecond)
    {
        if (shotsPerSecond <= 0f || ballistics.RecoilPerShotMoa <= 0f) return 0f;
        if (ballistics.RecoilDecayMoaPerSecond <= 0f) return ballistics.RecoilCapMoa;

        float sustainableRate = ballistics.RecoilDecayMoaPerSecond / ballistics.RecoilPerShotMoa;
        return shotsPerSecond >= sustainableRate
            ? ballistics.RecoilCapMoa
            : ballistics.RecoilPerShotMoa * 0.5f;
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
        float targetExposure,
        float extraMoa,
        float skillFactor)
    {
        if (WeaponConfig.Get(weapon) is not { } stats
            || BallisticsConfig.Get(weapon) is not { } ballistics
            || stats.Damage == 0)
            return default;

        float exposure = Math.Clamp(targetExposure, 0f, 1f);
        if (exposure <= 0f) return default;

        float targetRadius = GunConfig.HitRadius * MathF.Sqrt(exposure);
        float cyclic = CyclicShotsPerSecond(stats);
        var best = default(FiringSolution);

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
                SteadyStateRecoilMoa(ballistics, requested),
                extraMoa);

            float probability = HitEstimate.Probability(
                Spread.SigmaRadians(moa),
                range,
                targetRadius);

            float damagePerSecond = probability * stats.Damage * rate;
            if (damagePerSecond <= best.DamagePerSecond) continue;

            best = new FiringSolution(rate, moa, probability, damagePerSecond);
        }

        return best;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~WeaponEffectivenessTests"`
Expected: PASS, 11 tests.

If `FireDisciplineEmerges_SlowAtRange_FastUpClose` fails, the rate ladder is not biting: check that `SteadyStateRecoilMoa` is evaluated at `requested` and not at the derated `rate`. If `RifleHitProbabilityMatchesTheHitchmanCurveAtLongRange` fails high, `SightingMoa` for the sniper profile is too low — raise it rather than loosening the assertion, since the pruning bound in Task 4 depends on this not overestimating.

- [ ] **Step 5: Verify the whole fast suite**

Run: `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"`
Expected: PASS.

---

### Task 3: `CombatValue` — the currency

One function, one unit: net health points per second. This is the thing `ARCHITECTURE.md` says is missing.

**Files:**
- Create: `Common/Ai/CombatValue.cs`
- Test: `Common.Tests/CombatValueTests.cs` (create)

**Interfaces:**
- Consumes: `WeaponEffectiveness.Best` from Task 2.
- Produces:
  - `readonly record struct Engagement(float Range, ItemType TheirWeapon, float TheirExtraMoa, float MyExposureToThem, float TheirExposureToMe, float TheirTargetingLikelihood)`
  - `readonly record struct Combatant(ItemType Weapon, float ExtraMoa, float SkillFactor)`
  - `CombatValue.Dealt(in Combatant, IReadOnlyList<Engagement>) -> float`
  - `CombatValue.Taken(in Combatant, IReadOnlyList<Engagement>) -> float`
  - `CombatValue.Score(in Combatant, IReadOnlyList<Engagement>, float aggression) -> float`
  - `CombatValue.DefaultAggression` (float, 1)

- [ ] **Step 1: Write the failing test**

Create `Common.Tests/CombatValueTests.cs`:

```csharp
namespace Demiurge.Tests;

/// <summary>
/// The whole thesis of the AI overhaul, in one unit.
///
/// Every test here asserts that a piece of requested doctrine — "outrange and you hold", "outgunned
/// and you close", "digging is defensive" — falls out of the arithmetic rather than being written
/// down somewhere as a rule. If one of these fails, the currency is wrong, not the tuning.
/// </summary>
public class CombatValueTests
{
    private static Combatant Man(ItemType weapon, float extraMoa = 0f)
        => new(weapon, extraMoa, SkillFactor: 1f);

    private static Engagement Duel(
        ItemType theirWeapon,
        float range,
        float myExposure = 1f,
        float theirExposure = 1f,
        float theirExtraMoa = 0f)
        => new(range, theirWeapon, theirExtraMoa, theirExposure, myExposure, 1f);

    [Fact]
    public void HoldingBeatsClosingWhenYouOutrangeThem()
    {
        // A bolt gun against an SMG at 100 m. Holding is already near its ceiling; closing hands the
        // SMG its whole advantage.
        var me = Man(ItemType.Mosin);
        float holding = CombatValue.Score(me, [Duel(ItemType.Ppsh, 100f)], CombatValue.DefaultAggression);
        float closed = CombatValue.Score(me, [Duel(ItemType.Ppsh, 15f)], CombatValue.DefaultAggression);

        Assert.True(holding > closed, $"holding {holding:0.0} should beat closing {closed:0.0}");
    }

    [Fact]
    public void ClosingBeatsHoldingWhenTheyOutrangeYou()
    {
        var me = Man(ItemType.Ppsh);
        float holding = CombatValue.Score(me, [Duel(ItemType.Mosin, 100f)], CombatValue.DefaultAggression);
        float closed = CombatValue.Score(me, [Duel(ItemType.Mosin, 15f)], CombatValue.DefaultAggression);

        Assert.True(closed > holding, $"closing {closed:0.0} should beat holding {holding:0.0}");
    }

    [Fact]
    public void CoverIsWorthSomethingEvenWithNoOneShootingBack()
    {
        // This is why Entrench has value at all, and why it has NO value once you are already
        // protected: the term it improves is already at its floor.
        var me = Man(ItemType.Ak47);
        float exposed = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 1f)]);
        float behindCover = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 0.15f)]);

        Assert.True(behindCover < exposed);
        Assert.True(behindCover >= 0f);
    }

    [Fact]
    public void EntrenchingBuysNothingWhenAlreadyProtected()
    {
        // The castle case. Going from 5% exposed to 2% exposed is worth almost nothing, so any
        // action that costs time beats it. No "is there cover nearby" check is involved.
        var me = Man(ItemType.Ak47);
        float alreadySafe = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 0.05f)]);
        float dugIn = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 0.02f)]);
        float inTheOpen = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 1f)]);

        float gainWhenSafe = alreadySafe - dugIn;
        float gainWhenExposed = inTheOpen - dugIn;

        Assert.True(
            gainWhenSafe < gainWhenExposed * 0.1f,
            $"digging while protected gains {gainWhenSafe:0.00}, while exposed {gainWhenExposed:0.00}");
    }

    [Fact]
    public void SuppressingThemLowersWhatTheyCanDoToYou()
    {
        // The mechanism the whole squad layer rests on. If this ever stops being true, bounding
        // stops paying and the squad reverts to standing still.
        var me = Man(ItemType.Ak47);
        float unsuppressed = CombatValue.Taken(me, [Duel(ItemType.Mosin, 80f)]);
        float suppressed = CombatValue.Taken(
            me,
            [Duel(ItemType.Mosin, 80f, theirExtraMoa: BallisticsConfig.SuppressedMoa)]);

        Assert.True(
            suppressed < unsuppressed,
            $"suppressed {suppressed:0.00} must be below unsuppressed {unsuppressed:0.00}");
    }

    [Fact]
    public void AnEnemyLookingElsewhereIsLessDangerous()
    {
        var me = Man(ItemType.Ak47);
        var engaged = new Engagement(50f, ItemType.Ak47, 0f, 1f, 1f, TheirTargetingLikelihood: 1f);
        var distracted = engaged with { TheirTargetingLikelihood = 0.1f };

        Assert.True(CombatValue.Taken(me, [distracted]) < CombatValue.Taken(me, [engaged]));
    }

    [Fact]
    public void AggressionTradesSafetyForContact()
    {
        var me = Man(ItemType.Ppsh);
        var far = new[] { Duel(ItemType.Mosin, 100f) };

        float cautious = CombatValue.Score(me, far, aggression: 0.25f);
        float reckless = CombatValue.Score(me, far, aggression: 4f);

        Assert.True(reckless > cautious, "higher aggression must discount incoming damage");
    }

    [Fact]
    public void MoreEnemiesIsStrictlyWorse()
    {
        var me = Man(ItemType.Ak47);
        var one = new[] { Duel(ItemType.Ak47, 50f) };
        var three = new[] { Duel(ItemType.Ak47, 50f), Duel(ItemType.Ak47, 55f), Duel(ItemType.Ak47, 60f) };

        Assert.True(CombatValue.Taken(me, three) > CombatValue.Taken(me, one));
    }

    [Fact]
    public void NoEnemiesScoresZero()
        => Assert.Equal(0f, CombatValue.Score(Man(ItemType.Ak47), [], CombatValue.DefaultAggression));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~CombatValueTests"`
Expected: FAIL to compile — `The name 'CombatValue' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `Common/Ai/CombatValue.cs`:

```csharp
namespace Demiurge;

/// <summary>One side of a firefight, as scoring needs it.</summary>
/// <param name="Weapon">What they are carrying.</param>
/// <param name="ExtraMoa">Dispersion the caller already knows about — stance, movement,
/// suppression — combined in quadrature with the weapon's own.</param>
/// <param name="SkillFactor">Scales the sighting term. 1 is a competent soldier, above 1 is worse.
/// Deliberately affects execution only: scoring always uses the nominal value, so a poor shot does
/// not correctly reason about being a poor shot.</param>
public readonly record struct Combatant(ItemType Weapon, float ExtraMoa, float SkillFactor);

/// <summary>
/// One believed enemy, as seen from the scoring actor.
/// </summary>
/// <param name="Range">Metres between the two.</param>
/// <param name="TheirWeapon">What perception saw them carrying.</param>
/// <param name="TheirExtraMoa">Their stance/movement/suppression dispersion. Raising this is how
/// covering fire pays for a squadmate's bound.</param>
/// <param name="MyExposureToThem">Fraction of THEIR silhouette I can reach, 0..1.</param>
/// <param name="TheirExposureToMe">Fraction of MY silhouette they can reach, 0..1.</param>
/// <param name="TheirTargetingLikelihood">Probability they are shooting at me rather than at
/// somebody else, 0..1.</param>
public readonly record struct Engagement(
    float Range,
    ItemType TheirWeapon,
    float TheirExtraMoa,
    float MyExposureToThem,
    float TheirExposureToMe,
    float TheirTargetingLikelihood);

/// <summary>
/// The combat currency: net health points per second.
///
/// This is the unit ARCHITECTURE.md names as the open question — "seconds worked for movement
/// because execution time is a movement's honest cost, and combat has no equally obvious
/// equivalent". It does: the rate at which health changes hands.
///
/// It is what makes actions commensurable that otherwise are not. Holding, closing, flanking,
/// entrenching and suppressing all produce or prevent damage over time, so they can be compared
/// without anybody deciding in advance which one a rifleman should prefer. It also bridges to
/// navigation, which already prices routes in estimated execution seconds: a manoeuvre costing eight
/// seconds costs eight seconds of forgone dealt, plus whatever is taken in transit.
///
/// Pure, and in Common, so the doctrine can be tested headlessly.
/// </summary>
public static class CombatValue
{
    /// <summary>Neutral weighting of damage taken against damage dealt. Raising it makes the whole
    /// force close, flank and sprint; lowering it makes it hold and dig. It is the only global
    /// behaviour knob.</summary>
    public const float DefaultAggression = 1f;

    /// <summary>Health per second this actor puts into the enemies it can reach.</summary>
    public static float Dealt(in Combatant self, IReadOnlyList<Engagement> engagements)
    {
        float total = 0f;
        for (int i = 0; i < engagements.Count; i++)
        {
            var engagement = engagements[i];
            total += WeaponEffectiveness.Best(
                self.Weapon,
                engagement.Range,
                engagement.MyExposureToThem,
                self.ExtraMoa,
                self.SkillFactor).DamagePerSecond;
        }
        return total;
    }

    /// <summary>
    /// Health per second the enemies put into this actor.
    ///
    /// Their skill is deliberately nominal rather than their real skill: an actor cannot know how
    /// good a shot somebody else is, and assuming competence is the safe error.
    /// </summary>
    public static float Taken(in Combatant self, IReadOnlyList<Engagement> engagements)
    {
        float total = 0f;
        for (int i = 0; i < engagements.Count; i++)
        {
            var engagement = engagements[i];
            total += WeaponEffectiveness.Best(
                    engagement.TheirWeapon,
                    engagement.Range,
                    engagement.TheirExposureToMe,
                    engagement.TheirExtraMoa,
                    skillFactor: 1f).DamagePerSecond
                * Math.Clamp(engagement.TheirTargetingLikelihood, 0f, 1f);
        }
        return total;
    }

    /// <summary>
    /// Net health per second: what this actor gains minus what it risks, discounted by aggression.
    ///
    /// Every candidate action is scored by building the engagement list it would produce and calling
    /// this. Nothing else decides behaviour.
    /// </summary>
    public static float Score(
        in Combatant self,
        IReadOnlyList<Engagement> engagements,
        float aggression)
        => Dealt(self, engagements)
           - Taken(self, engagements) / MathF.Max(aggression, 0.01f);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~CombatValueTests"`
Expected: PASS, 9 tests.

`HoldingBeatsClosingWhenYouOutrangeThem` is the load-bearing one. If it fails, `SightingMoa` for the pistol profile is too low — the PPSh is still competitive at 100 m, which is the exact failure that forced `MaxEngagementRangeFor` to exist. Raise the pistol sighting term until the ordering holds, then re-run Task 2's Hitchman assertion to confirm the rifle end is still calibrated.

- [ ] **Step 5: Verify the whole fast suite**

Run: `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"`
Expected: PASS.

---

### Task 4: Sound threat pruning

Scoring needs `MyExposureToThem` and `TheirExposureToMe` per enemy, and exposure is the only term needing a raycast. Perception casts one ray per NPC per tick. This task decides which enemy gets it — and does so with a provable bound rather than a heuristic, so a dismissed enemy is dismissed for a stated reason.

**Files:**
- Create: `Common/Ai/ThreatRanking.cs`
- Test: `Common.Tests/ThreatRankingTests.cs` (create)

**Interfaces:**
- Consumes: `WeaponEffectiveness.Best`, `Engagement` from Tasks 2–3.
- Produces:
  - `readonly record struct ThreatBound(int Index, float UpperBound)`
  - `ThreatRanking.UpperBound(in Engagement) -> float`
  - `ThreatRanking.Rank(IReadOnlyList<Engagement>, Span<ThreatBound> into) -> int`

- [ ] **Step 1: Write the failing test**

Create `Common.Tests/ThreatRankingTests.cs`:

```csharp
namespace Demiurge.Tests;

/// <summary>
/// Which enemy is worth spending a raycast on.
///
/// The point is that this is not a heuristic. Exposure is the only term in Taken that needs a ray,
/// and exposure is at most 1, so the exposure-free product is a genuine UPPER BOUND on what that
/// enemy could contribute. An enemy below the threshold provably cannot change the decision by more
/// than the threshold — which is a much stronger statement than "SMGs far away probably don't
/// matter", and it is what lets the ray budget be spent top-down without hiding a real threat.
/// </summary>
public class ThreatRankingTests
{
    private static Engagement At(ItemType weapon, float range, float exposure = 1f)
        => new(range, weapon, 0f, 1f, exposure, 1f);

    [Fact]
    public void TheBoundIsNeverBelowTheTrueContribution()
    {
        var me = new Combatant(ItemType.Ak47, 0f, 1f);

        foreach (var weapon in new[] { ItemType.Ppsh, ItemType.Ak47, ItemType.Sks, ItemType.Mosin })
            for (float range = 5f; range <= 250f; range += 5f)
                foreach (float exposure in new[] { 0f, 0.05f, 0.3f, 0.75f, 1f })
                {
                    var engagement = At(weapon, range, exposure);
                    float bound = ThreatRanking.UpperBound(engagement);
                    float actual = CombatValue.Taken(me, [engagement]);

                    Assert.True(
                        bound >= actual - 1e-3f,
                        $"{weapon} at {range} m, exposure {exposure}: bound {bound:0.000} < actual {actual:0.000}");
                }
    }

    [Fact]
    public void ASubmachineGunAcrossTheMapSinksToTheBottom()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[3];
        int count = ThreatRanking.Rank(
            [
                At(ItemType.Ppsh, 100f),   // index 0 — far SMG, nearly harmless
                At(ItemType.Mosin, 100f),  // index 1 — far bolt gun, dangerous
                At(ItemType.Ppsh, 8f),     // index 2 — SMG in your face
            ],
            ranked);

        Assert.Equal(3, count);
        Assert.Equal(2, ranked[0].Index);
        Assert.Equal(0, ranked[2].Index);
    }

    [Fact]
    public void RankingIsSortedDescending()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[4];
        int count = ThreatRanking.Rank(
            [At(ItemType.Ak47, 60f), At(ItemType.Ak47, 10f), At(ItemType.Ak47, 120f), At(ItemType.Ak47, 30f)],
            ranked);

        for (int i = 1; i < count; i++)
            Assert.True(ranked[i - 1].UpperBound >= ranked[i].UpperBound);
    }

    [Fact]
    public void AnEnemyLookingElsewhereRanksLower()
    {
        var focused = At(ItemType.Ak47, 40f);
        var distracted = focused with { TheirTargetingLikelihood = 0.05f };

        Assert.True(ThreatRanking.UpperBound(distracted) < ThreatRanking.UpperBound(focused));
    }

    [Fact]
    public void RankingStopsAtTheDestinationCapacity()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[2];
        int count = ThreatRanking.Rank(
            [At(ItemType.Ak47, 60f), At(ItemType.Ak47, 10f), At(ItemType.Ak47, 120f)],
            ranked);

        Assert.Equal(2, count);
        Assert.Equal(1, ranked[0].Index);
    }

    [Fact]
    public void NoThreatsRanksNothing()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[4];
        Assert.Equal(0, ThreatRanking.Rank([], ranked));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~ThreatRankingTests"`
Expected: FAIL to compile — `The name 'ThreatRanking' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `Common/Ai/ThreatRanking.cs`:

```csharp
namespace Demiurge;

/// <summary>An enemy's index in the caller's list, and the most it could possibly be worth.</summary>
public readonly record struct ThreatBound(int Index, float UpperBound);

/// <summary>
/// Deciding which believed enemies are worth spending perception on.
///
/// Every term in <see cref="CombatValue.Taken"/> except exposure is a table lookup needing no ray,
/// and exposure is at most 1. So evaluating the rest with exposure pinned at 1 gives a true UPPER
/// BOUND on that enemy's contribution. Rank by the bound, spend the ray budget from the top, and an
/// enemy left unexamined is one that provably could not have changed the decision by more than the
/// threshold.
///
/// That soundness is the point. "An SMG 100 m away probably doesn't matter" is a guess that fails
/// quietly when it is wrong; "this enemy's maximum possible contribution is 0.4 HP/s" is a fact. It
/// is also why WeaponEffectiveness must not overestimate hit probability — the bound inherits any
/// optimism in the curve.
///
/// Doubles as target selection: the same ranking read from the other end is "most dangerous to me
/// right now", so one sorted list serves both.
/// </summary>
public static class ThreatRanking
{
    /// <summary>
    /// The most this enemy could take from us, if it turned out to be looking straight at a fully
    /// exposed target. No raycast.
    /// </summary>
    public static float UpperBound(in Engagement engagement)
        => WeaponEffectiveness.Best(
                engagement.TheirWeapon,
                engagement.Range,
                targetExposure: 1f,
                engagement.TheirExtraMoa,
                skillFactor: 1f).DamagePerSecond
           * Math.Clamp(engagement.TheirTargetingLikelihood, 0f, 1f);

    /// <summary>
    /// Fills <paramref name="into"/> with the most dangerous enemies first, and returns how many
    /// were written. Writes at most <c>into.Length</c> entries, so a caller with a ray budget passes
    /// a span that size and gets exactly the enemies worth spending it on.
    /// </summary>
    public static int Rank(IReadOnlyList<Engagement> engagements, Span<ThreatBound> into)
    {
        if (into.Length == 0 || engagements.Count == 0) return 0;

        // Insertion into a bounded, already-sorted destination. The budget is single digits, so this
        // beats allocating and sorting the full list every tick for every NPC.
        int count = 0;
        for (int i = 0; i < engagements.Count; i++)
        {
            var candidate = new ThreatBound(i, UpperBound(engagements[i]));
            if (count == into.Length && candidate.UpperBound <= into[count - 1].UpperBound)
                continue;

            int position = count < into.Length ? count : count - 1;
            while (position > 0 && into[position - 1].UpperBound < candidate.UpperBound)
            {
                into[position] = into[position - 1];
                position--;
            }
            into[position] = candidate;
            if (count < into.Length) count++;
        }

        return count;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~ThreatRankingTests"`
Expected: PASS, 6 tests. `TheBoundIsNeverBelowTheTrueContribution` sweeps 4 weapons × 50 ranges × 5 exposures = 1000 cases.

- [ ] **Step 5: Verify the whole fast suite**

Run: `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"`
Expected: PASS.

---

### Task 5: Gunshot memory that keeps the shot that matters

`MobBrain` holds one heard-shot slot and overwrites it on every new shot, so a shot 55 m away erases one fired 3 m behind the NPC's head. That is the reported "NPCs are unaware of a player shooting right behind them".

**Files:**
- Create: `Common/Ai/HeardShots.cs`
- Modify: `Common/Ai/GunshotHearing.cs`
- Test: `Common.Tests/HeardShotsTests.cs` (create)

**Interfaces:**
- Consumes: `GunshotHearing.MaximumDistance`, `GunshotHearing.InvestigationTicks`.
- Produces:
  - `GunshotHearing.LocalisationError(float distance) -> float`
  - `GunshotHearing.PerceivedPosition(Vector3 listener, Vector3 shot, uint seed) -> Vector3`
  - `readonly record struct HeardShot(ushort ShooterId, Vector3 Position, uint Tick, float Salience)`
  - `HeardShots` class: `Hear(ushort, Vector3 perceived, float distance, uint tick)`, `TryMostSalient(uint tick, out HeardShot)`, `Prune(uint tick)`, `Clear()`, `Count`

- [ ] **Step 1: Write the failing test**

Create `Common.Tests/HeardShotsTests.cs`:

```csharp
using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// What an NPC remembers about gunfire.
///
/// The bug being fixed: a single last-write-wins slot means a distant shot erases a point-blank one,
/// so an NPC forgets the player standing behind it because somebody fired across the map a tick
/// later. Memory must be ranked by how much the shot matters, not by which arrived last.
/// </summary>
public class HeardShotsTests
{
    [Fact]
    public void APointBlankShotSurvivesADistantOneArrivingLater()
    {
        var shots = new HeardShots();
        shots.Hear(shooterId: 7, new Vector3(2f, 0f, 0f), distance: 2f, tick: 100);
        shots.Hear(shooterId: 9, new Vector3(55f, 0f, 0f), distance: 55f, tick: 101);

        Assert.True(shots.TryMostSalient(101, out var best));
        Assert.Equal(7, best.ShooterId);
    }

    [Fact]
    public void ACloserShotDisplacesAnEarlierDistantOne()
    {
        var shots = new HeardShots();
        shots.Hear(9, new Vector3(55f, 0f, 0f), 55f, 100);
        shots.Hear(7, new Vector3(2f, 0f, 0f), 2f, 101);

        Assert.True(shots.TryMostSalient(101, out var best));
        Assert.Equal(7, best.ShooterId);
    }

    [Fact]
    public void RepeatedFireFromOneShooterDoesNotFillTheMemory()
    {
        var shots = new HeardShots();
        for (uint tick = 0; tick < 30; tick++)
            shots.Hear(7, new Vector3(20f, 0f, 0f), 20f, tick);

        Assert.Equal(1, shots.Count);
    }

    [Fact]
    public void ShotsExpireAfterTheInvestigationWindow()
    {
        var shots = new HeardShots();
        shots.Hear(7, Vector3.Zero, 5f, tick: 10);

        uint expired = 10 + GunshotHearing.InvestigationTicks;
        shots.Prune(expired);

        Assert.False(shots.TryMostSalient(expired, out _));
        Assert.Equal(0, shots.Count);
    }

    [Fact]
    public void NothingHeardMeansNothingToReport()
        => Assert.False(new HeardShots().TryMostSalient(0, out _));

    [Fact]
    public void LocalisationErrorGrowsWithDistanceAndIsNearlyExactUpClose()
    {
        float close = GunshotHearing.LocalisationError(2f);
        float far = GunshotHearing.LocalisationError(GunshotHearing.MaximumDistance);

        Assert.True(close < 1f, $"a shot 2 m away should localise within a metre, got {close}");
        Assert.True(far > close * 5f, $"a shot at max range should be vague, got {far}");
    }

    [Fact]
    public void PerceivedPositionStaysWithinTheErrorOfTheTruth()
    {
        var listener = Vector3.Zero;
        var truth = new Vector3(40f, 0f, 0f);

        for (uint seed = 1; seed < 200; seed++)
        {
            var perceived = GunshotHearing.PerceivedPosition(listener, truth, seed);
            float slip = Vector3.Distance(perceived, truth);
            Assert.True(
                slip <= GunshotHearing.LocalisationError(40f) + 1e-3f,
                $"seed {seed} slipped {slip:0.00} m");
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~HeardShotsTests"`
Expected: FAIL to compile — `HeardShots` and `GunshotHearing.LocalisationError` do not exist.

- [ ] **Step 3: Extend `GunshotHearing`**

In `Common/Ai/GunshotHearing.cs`, add below `CanHear`:

```csharp
    /// <summary>
    /// How far a heard shot's perceived position can sit from the truth, at a given distance.
    ///
    /// Deliberately near-zero up close. A player firing a few metres behind an NPC must be located,
    /// not merely noticed — that specific failure is the reason this exists.
    /// </summary>
    public const float MinimumError = 0.5f;
    public const float MaximumError = 12f;

    public static float LocalisationError(float distance)
    {
        float t = Math.Clamp(distance / MaximumDistance, 0f, 1f);
        return MinimumError + (MaximumError - MinimumError) * t * t;
    }

    /// <summary>
    /// Where the listener thinks the shot came from. Deterministic in <paramref name="seed"/> so a
    /// replay or a test sees the same answer twice; the offset is horizontal because a listener
    /// misjudges bearing and range, not elevation.
    /// </summary>
    public static Vector3 PerceivedPosition(Vector3 listener, Vector3 shot, uint seed)
    {
        float distance = Vector3.Distance(listener, shot);
        float error = LocalisationError(distance);

        uint state = seed == 0 ? 0x9e3779b9u : seed;
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        float angle = state / (float)uint.MaxValue * MathF.Tau;
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        float radius = MathF.Sqrt(state / (float)uint.MaxValue) * error;

        return shot + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
    }
```

Add `using System.Numerics;` at the top of the file if it is not already present.

- [ ] **Step 4: Write `HeardShots`**

Create `Common/Ai/HeardShots.cs`:

```csharp
using System.Numerics;

namespace Demiurge;

/// <summary>One remembered gunshot, and how much it deserves attention.</summary>
public readonly record struct HeardShot(
    ushort ShooterId,
    Vector3 Position,
    uint Tick,
    float Salience);

/// <summary>
/// Short-term memory of enemy gunfire, ranked by salience rather than by recency.
///
/// It replaces a single last-write-wins slot on MobBrain, which had the specific failure that a
/// distant shot erased a point-blank one — so an NPC forgot the player standing behind it because
/// somebody fired across the map on the next tick. A shot fired two metres away is not the same
/// event as one fired fifty metres away, and recency cannot express the difference.
///
/// Small and fixed-size: this is a handful of entries per NPC, kept for a few seconds, and there are
/// 32 NPCs. One entry per shooter, because a man firing a magazine is one contact, not thirty.
/// </summary>
public sealed class HeardShots
{
    /// <summary>Enough to hold every shooter an NPC can realistically be tracking at once. Beyond
    /// this the quietest is dropped, which is the correct thing to forget.</summary>
    public const int Capacity = 4;

    private readonly HeardShot[] shots = new HeardShot[Capacity];
    private int count;

    public int Count => count;

    /// <summary>Loudness of a shot at this distance: 1 in your ear, 0 at the edge of hearing.</summary>
    public static float SalienceAt(float distance)
    {
        float t = Math.Clamp(distance / GunshotHearing.MaximumDistance, 0f, 1f);
        return (1f - t) * (1f - t);
    }

    public void Hear(ushort shooterId, Vector3 perceived, float distance, uint tick)
    {
        var shot = new HeardShot(shooterId, perceived, tick, SalienceAt(distance));

        for (int i = 0; i < count; i++)
        {
            if (shots[i].ShooterId != shooterId) continue;
            // Same man firing again: refresh rather than accumulate. A magazine is one contact.
            shots[i] = shot;
            return;
        }

        if (count < Capacity)
        {
            shots[count++] = shot;
            return;
        }

        int quietest = 0;
        for (int i = 1; i < count; i++)
            if (shots[i].Salience < shots[quietest].Salience) quietest = i;

        if (shot.Salience > shots[quietest].Salience) shots[quietest] = shot;
    }

    /// <summary>The shot most worth reacting to, ignoring any that have gone stale.</summary>
    public bool TryMostSalient(uint tick, out HeardShot shot)
    {
        shot = default;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            if (Expired(shots[i], tick)) continue;
            if (found && shots[i].Salience <= shot.Salience) continue;

            shot = shots[i];
            found = true;
        }

        return found;
    }

    public void Prune(uint tick)
    {
        int kept = 0;
        for (int i = 0; i < count; i++)
            if (!Expired(shots[i], tick))
                shots[kept++] = shots[i];

        for (int i = kept; i < count; i++) shots[i] = default;
        count = kept;
    }

    public void Clear()
    {
        Array.Clear(shots);
        count = 0;
    }

    private static bool Expired(in HeardShot shot, uint tick)
        => tick >= shot.Tick && tick - shot.Tick >= GunshotHearing.InvestigationTicks;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Common.Tests/DemiurgeCommon.Tests.csproj --filter "FullyQualifiedName~HeardShotsTests|FullyQualifiedName~GunshotHearingTests"`
Expected: PASS. The existing `GunshotHearingTests` must still pass — `CanHear` is unchanged.

- [ ] **Step 6: Verify the whole fast suite**

Run: `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"`
Expected: PASS.

---

### Task 6: Squad allocation replaces squad doctrine

`SquadTactics.Plan` currently refuses to issue a `Bound` order unless some member reports `IsSet`, and `MobBrain.IsSet => AtCover`. A squad that cannot reach cover is forbidden from moving and stands up — the reported flagpost deadlock. This task replaces the doctrine with an allocation that always returns an assignment.

The key structural change: a member's value as a suppressor is mostly the damage it *prevents* to a mover, not the damage it deals. That term only exists in a joint score, which is why scoring each man independently produced N individuals who each concluded that moving was dangerous.

**Files:**
- Rewrite: `Server/Ai/SquadTactics.cs`
- Rewrite: `Server.Tests/SquadTacticsTests.cs`

**Interfaces:**
- Consumes: `CombatValue`, `Combatant`, `Engagement` (Task 3).
- Produces:
  - `enum SquadRole { None, BaseOfFire, Bound }` (unchanged)
  - `readonly record struct SquadMemberState(ushort ActorId, Vector3 Position, ItemType Weapon, float ExposureHere, float SkillFactor, int BoundIndex, uint MovingSinceTick)`
  - `readonly record struct SquadPlanInput(Vector3 Threat, bool HasThreat, ItemType ThreatWeapon, float Aggression, uint Tick)`
  - `readonly record struct SquadTacticalOrder(ushort ActorId, SquadRole Role, Vector3 Destination, float Bearing, int BoundIndex)`
  - `SquadTactics.Plan(in SquadPlanInput, IReadOnlyList<SquadMemberState>, List<SquadTacticalOrder>)`
  - `SquadTactics.MoverTimeoutTicks` (uint)

- [ ] **Step 1: Write the failing test**

Replace `Server.Tests/SquadTacticsTests.cs` entirely:

```csharp
using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// Properties of the squad model, not traces through it.
///
/// The behaviour being replaced had a gate: nobody moved until somebody was "set", and set meant
/// "at cover". A squad that could not reach cover was therefore forbidden from moving and stood in
/// the open — which is the flagpost deadlock, the aimless digging, and the general staticness all at
/// once. The replacement has no gate: the allocation always returns an assignment, and its worst
/// case is everyone shooting, never everyone waiting.
/// </summary>
public class SquadTacticsTests
{
    private static readonly Vector3 Threat = new(0f, 0f, 60f);

    private static SquadMemberState Man(
        ushort id,
        float x,
        float z,
        ItemType weapon = ItemType.Ak47,
        float exposure = 1f,
        int boundIndex = 0,
        uint movingSince = 0)
        => new(id, new Vector3(x, 0f, z), weapon, exposure, SkillFactor: 1f, boundIndex, movingSince);

    private static List<SquadTacticalOrder> Plan(
        ItemType threatWeapon = ItemType.Ak47,
        uint tick = 0,
        params SquadMemberState[] members)
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(
            new SquadPlanInput(Threat, HasThreat: true, threatWeapon, CombatValue.DefaultAggression, tick),
            members,
            orders);
        return orders;
    }

    private static SquadTacticalOrder For(List<SquadTacticalOrder> orders, ushort id)
        => orders.Single(order => order.ActorId == id);

    [Fact]
    public void EveryMemberAlwaysGetsAnOrder()
    {
        var orders = Plan(members: [Man(1, 0, 0), Man(2, 4, 0), Man(3, 8, 0), Man(4, 12, 0)]);
        Assert.Equal(4, orders.Count);
        Assert.All(orders, order => Assert.NotEqual(SquadRole.None, order.Role));
    }

    /// <summary>
    /// The anti-deadlock property, and the reason this rewrite exists. Stated as a property of the
    /// model rather than as "the IsSet gate is gone", so it keeps holding however the allocation is
    /// later reorganised.
    /// </summary>
    [Fact]
    public void NobodyBeingInCoverDoesNotFreezeTheSquad()
    {
        var orders = Plan(members:
        [
            Man(1, 0, 0, exposure: 1f),
            Man(2, 4, 0, exposure: 1f),
            Man(3, 8, 0, exposure: 1f),
            Man(4, 12, 0, exposure: 1f),
        ]);

        Assert.Contains(orders, order => order.Role == SquadRole.Bound);
    }

    [Fact]
    public void SomebodyIsAlwaysShootingWhileSomebodyMoves()
    {
        var orders = Plan(members: [Man(1, 0, 0), Man(2, 4, 0), Man(3, 8, 0), Man(4, 12, 0)]);

        Assert.Contains(orders, order => order.Role == SquadRole.BaseOfFire);
        Assert.Contains(orders, order => order.Role == SquadRole.Bound);
    }

    [Fact]
    public void ALoneManNeverBoundsWithNobodyCoveringHim()
    {
        // Degenerate case: with no one to suppress, moving in the open is pure loss.
        var orders = Plan(members: [Man(1, 0, 0)]);
        Assert.Equal(SquadRole.BaseOfFire, For(orders, 1).Role);
    }

    [Fact]
    public void MenWhoOutrangeTheThreatHoldWhileTheShortRangedOnesClose()
    {
        var orders = Plan(
            threatWeapon: ItemType.Ak47,
            members:
            [
                Man(1, 0, 0, ItemType.Mosin),
                Man(2, 4, 0, ItemType.Mosin),
                Man(3, 8, 0, ItemType.Ppsh),
                Man(4, 12, 0, ItemType.Ppsh),
            ]);

        Assert.Equal(SquadRole.BaseOfFire, For(orders, 1).Role);
        Assert.Equal(SquadRole.BaseOfFire, For(orders, 2).Role);
        Assert.Contains(orders, o => o.Role == SquadRole.Bound && o.ActorId is 3 or 4);
    }

    [Fact]
    public void MoversGoOnDifferentBearings()
    {
        var orders = Plan(members:
            [Man(1, -8, 0), Man(2, -4, 0), Man(3, 4, 0), Man(4, 8, 0), Man(5, 12, 0), Man(6, 16, 0)]);

        var bearings = orders
            .Where(order => order.Role == SquadRole.Bound)
            .Select(order => order.Bearing)
            .ToList();

        if (bearings.Count >= 2)
            Assert.True(
                bearings.Distinct().Count() == bearings.Count,
                "two movers must not share a bearing — the same cover would defeat both");
    }

    [Fact]
    public void AMoverWhoNeverArrivesReleasesTheRotation()
    {
        uint now = SquadTactics.MoverTimeoutTicks + 10;
        var orders = Plan(
            tick: now,
            members:
            [
                Man(1, 0, 0, movingSince: 1),   // has been moving far too long
                Man(2, 4, 0),
                Man(3, 8, 0),
                Man(4, 12, 0),
            ]);

        Assert.Equal(SquadRole.BaseOfFire, For(orders, 1).Role);
        Assert.Contains(orders, order => order.Role == SquadRole.Bound && order.ActorId != 1);
    }

    [Fact]
    public void TheManFurthestBackTakesTheNextBound()
    {
        var orders = Plan(members:
            [Man(1, 0, 50), Man(2, 0, 40), Man(3, 0, 10), Man(4, 0, 0)]);

        var mover = orders.Single(order => order.Role == SquadRole.Bound);
        Assert.Equal(4, mover.ActorId);
    }

    [Fact]
    public void NoThreatMeansNoCombatRoles()
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(
            new SquadPlanInput(Vector3.Zero, HasThreat: false, ItemType.Ak47, CombatValue.DefaultAggression, 0),
            [Man(1, 0, 0), Man(2, 4, 0)],
            orders);

        Assert.All(orders, order => Assert.Equal(SquadRole.None, order.Role));
    }

    [Fact]
    public void AnEmptySquadPlansNothing()
    {
        var orders = new List<SquadTacticalOrder>();
        SquadTactics.Plan(
            new SquadPlanInput(Threat, true, ItemType.Ak47, CombatValue.DefaultAggression, 0),
            [],
            orders);

        Assert.Empty(orders);
    }

    /// <summary>
    /// The number the whole squad layer hangs on. If BallisticsConfig.SuppressedMoa is ever tuned
    /// down far enough that covering fire stops mattering, bounding stops paying and the squad
    /// silently reverts to standing still — the exact failure this rewrite exists to remove. This
    /// asserts it directly so the number fails loudly instead.
    /// </summary>
    [Fact]
    public void CoveringFireIsWhatMakesABoundAffordable()
    {
        var mover = new Combatant(ItemType.Ak47, 0f, 1f);
        var inTheOpen = new Engagement(60f, ItemType.Ak47, 0f, 1f, 1f, 1f);
        var suppressed = inTheOpen with { TheirExtraMoa = BallisticsConfig.SuppressedMoa };

        float exposedCost = CombatValue.Taken(mover, [inTheOpen]);
        float coveredCost = CombatValue.Taken(mover, [suppressed]);

        Assert.True(
            coveredCost < exposedCost * 0.8f,
            $"covering fire only removed {1f - coveredCost / exposedCost:P0} of incoming damage — "
            + "not enough for a bound to price out");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~SquadTacticsTests"`
Expected: FAIL to compile — `SquadMemberState`, `SquadPlanInput` and the new `Plan` overload do not exist.

- [ ] **Step 3: Rewrite `SquadTactics`**

Replace `Server/Ai/SquadTactics.cs` entirely:

```csharp
using System.Numerics;

namespace Demiurge.GameServer;

internal enum SquadRole : byte
{
    /// <summary>No believed threat. The member follows its objective normally.</summary>
    None,

    /// <summary>Hold and shoot. Its value is mostly the damage it PREVENTS to whoever is moving.</summary>
    BaseOfFire,

    /// <summary>Move to a new bearing while the rest of the squad shoots.</summary>
    Bound,
}

/// <summary>One member, as the allocation needs to see him.</summary>
/// <param name="ExposureHere">Fraction of his silhouette the threat can reach where he stands.</param>
/// <param name="MovingSinceTick">When his current bound began, or 0 if he is not moving. A mover who
/// cannot arrive must not hold the rotation shut.</param>
internal readonly record struct SquadMemberState(
    ushort ActorId,
    Vector3 Position,
    ItemType Weapon,
    float ExposureHere,
    float SkillFactor,
    int BoundIndex,
    uint MovingSinceTick);

internal readonly record struct SquadPlanInput(
    Vector3 Threat,
    bool HasThreat,
    ItemType ThreatWeapon,
    float Aggression,
    uint Tick);

/// <summary>
/// What a member has been told to do. <paramref name="Bearing"/> is the approach angle in radians
/// around the threat, so two movers on different bearings cannot be stopped by the same cover.
/// </summary>
internal readonly record struct SquadTacticalOrder(
    ushort ActorId,
    SquadRole Role,
    Vector3 Destination,
    float Bearing,
    int BoundIndex);

/// <summary>
/// Who does which, priced in the same currency as everything else.
///
/// This is an ALLOCATION, not a doctrine. It does not decide that squads bound, or that riflemen
/// hold and SMGs close; it computes what each member is worth in each role and picks the assignment
/// with the highest squad total. Those behaviours then appear because the numbers say so.
///
/// The reason it must be joint rather than per-member: a base of fire's value is mostly the damage
/// it PREVENTS to a mover, by inflating the threat's dispersion (BallisticsConfig.SuppressedMoa).
/// Scored individually, suppressing an enemy you cannot reliably hit looks worthless, every man
/// independently concludes that moving is dangerous, and the whole squad stands still — which is
/// precisely the behaviour this replaces.
///
/// There is deliberately NO precondition on movement. The previous version refused to issue a bound
/// unless somebody was already at cover, so a squad that could not reach cover was forbidden from
/// moving and stood in the open digging. The allocation here always returns an assignment; its worst
/// case is that everybody shoots.
///
/// Pure and deterministic, so the doctrine can be tested headlessly. Terrain is absent by design:
/// this produces intent, and navigation and cover selection resolve it against the real field.
/// </summary>
internal static class SquadTactics
{
    /// <summary>Standoff for the first bound, and how much closer each one gets.</summary>
    public const float OpeningStandoff = 45f;
    public const float BoundLength = 12f;
    public const float MinimumStandoff = 12f;

    /// <summary>
    /// Bearings a mover may approach on, in radians either side of the threat axis. Wide enough that
    /// one piece of cover cannot defeat two of them, which is the only reason to spread at all.
    /// </summary>
    private static readonly float[] Bearings =
        [-1.05f, 1.05f, -0.52f, 0.52f, -1.57f, 1.57f];

    /// <summary>
    /// How long a man may be moving before the rotation stops waiting for him.
    ///
    /// Self-clocking rotation — the next man goes when the last one arrives — deadlocks whenever a
    /// mover cannot arrive, and "cannot arrive" is common: pinned, blocked, or sent somewhere that
    /// turned out unreachable. This is the escape hatch, and it is why the ungated allocation above
    /// is not enough on its own.
    /// </summary>
    public const uint MoverTimeoutTicks = 5 * NetworkConfig.TickRate;

    /// <summary>Movers allowed at once. One per bearing, and never everybody.</summary>
    private const int MaxMovers = 2;

    public static void Plan(
        in SquadPlanInput input,
        IReadOnlyList<SquadMemberState> members,
        List<SquadTacticalOrder> orders)
    {
        orders.Clear();
        if (members.Count == 0) return;

        if (!input.HasThreat)
        {
            foreach (var member in members)
                orders.Add(new SquadTacticalOrder(
                    member.ActorId, SquadRole.None, Vector3.Zero, 0f, member.BoundIndex));
            return;
        }

        Span<float> holdValue = stackalloc float[members.Count];
        Span<float> moveValue = stackalloc float[members.Count];
        Span<bool> timedOut = stackalloc bool[members.Count];

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            float range = Horizontal(member.Position, input.Threat);

            // What he is worth standing where he is.
            holdValue[i] = CombatValue.Score(
                new Combatant(member.Weapon, 0f, member.SkillFactor),
                [Engagement(input, range, member.ExposureHere)],
                input.Aggression);

            // What he would be worth one bound closer, paying transit exposure to get there.
            float closer = MathF.Max(MinimumStandoff, range - BoundLength);
            moveValue[i] = CombatValue.Score(
                new Combatant(member.Weapon, 0f, member.SkillFactor),
                [Engagement(input, closer, exposure: 1f)],
                input.Aggression);

            timedOut[i] = member.MovingSinceTick != 0
                && input.Tick >= member.MovingSinceTick
                && input.Tick - member.MovingSinceTick >= MoverTimeoutTicks;
        }

        // Suppression the squad can apply if a given man leaves the firing line. Measured as what a
        // mover's incoming damage drops to once the threat's dispersion is inflated — the term that
        // does not exist unless the squad is scored jointly.
        float coveredCost = CombatValue.Taken(
            new Combatant(members[0].Weapon, 0f, 1f),
            [Engagement(input, OpeningStandoff, 1f, BallisticsConfig.SuppressedMoa)]);
        float exposedCost = CombatValue.Taken(
            new Combatant(members[0].Weapon, 0f, 1f),
            [Engagement(input, OpeningStandoff, 1f)]);
        float suppressionWorth = MathF.Max(0f, exposedCost - coveredCost);

        // Rank by how much each man gains from moving, net of what the squad loses in fire. A man
        // already close and shooting well has little to gain; the man furthest back has most.
        var candidates = new List<(int Index, float Gain)>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            if (timedOut[i]) continue;
            candidates.Add((i, moveValue[i] - holdValue[i] + suppressionWorth));
        }

        candidates.Sort((left, right) =>
        {
            int byGain = right.Gain.CompareTo(left.Gain);
            if (byGain != 0) return byGain;
            // Deterministic tie-break: the man furthest from the threat moves first, then by id.
            float leftRange = Horizontal(members[left.Index].Position, input.Threat);
            float rightRange = Horizontal(members[right.Index].Position, input.Threat);
            int byRange = rightRange.CompareTo(leftRange);
            return byRange != 0
                ? byRange
                : members[left.Index].ActorId.CompareTo(members[right.Index].ActorId);
        });

        // A lone man has nobody to cover him, so moving in the open is pure loss. Anything larger
        // keeps at least one gun on the threat.
        int allowedMovers = members.Count <= 1
            ? 0
            : Math.Min(MaxMovers, members.Count - 1);

        var movers = new Dictionary<int, float>(allowedMovers);
        int bearingCursor = 0;
        foreach (var (index, gain) in candidates)
        {
            if (movers.Count >= allowedMovers) break;
            if (gain <= 0f) continue;
            movers[index] = Bearings[bearingCursor++ % Bearings.Length];
        }

        Vector3 axis = Horizontal(input.Threat - Centre(members));
        axis = axis.LengthSquared() <= 1e-6f ? Vector3.UnitZ : Vector3.Normalize(axis);

        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (!movers.TryGetValue(i, out float bearing))
            {
                orders.Add(new SquadTacticalOrder(
                    member.ActorId, SquadRole.BaseOfFire, member.Position, 0f, member.BoundIndex));
                continue;
            }

            orders.Add(new SquadTacticalOrder(
                member.ActorId,
                SquadRole.Bound,
                ApproachPosition(input.Threat, axis, bearing, member.BoundIndex),
                bearing,
                member.BoundIndex));
        }
    }

    /// <summary>
    /// Where a bound ends: on its own bearing around the threat, and closer with every bound
    /// completed, so the squad converges rather than orbiting.
    /// </summary>
    public static Vector3 ApproachPosition(Vector3 threat, Vector3 axis, float bearing, int boundIndex)
    {
        float standoff = MathF.Max(
            MinimumStandoff,
            OpeningStandoff - MathF.Max(0, boundIndex) * BoundLength);

        float cos = MathF.Cos(bearing);
        float sin = MathF.Sin(bearing);
        var rotated = new Vector3(
            axis.X * cos - axis.Z * sin,
            0f,
            axis.X * sin + axis.Z * cos);

        return threat - rotated * standoff;
    }

    /// <summary>Named <c>Against</c>, not <c>Engagement</c>: a method sharing a name with the type it
    /// returns makes every call site inside a collection expression ambiguous to read.</summary>
    private static Engagement Against(
        in SquadPlanInput input,
        float range,
        float exposure,
        float threatExtraMoa = 0f)
        => new(range, input.ThreatWeapon, threatExtraMoa, 1f, exposure, 1f);

    private static Vector3 Centre(IReadOnlyList<SquadMemberState> members)
    {
        Vector3 sum = Vector3.Zero;
        foreach (var member in members) sum += member.Position;
        return sum / members.Count;
    }

    private static Vector3 Horizontal(Vector3 value) => value with { Y = 0f };

    private static float Horizontal(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~SquadTacticsTests"`
Expected: PASS, 11 tests.

`CoveringFireIsWhatMakesABoundAffordable` is the diagnostic one. If it fails, `BallisticsConfig.SuppressedMoa = 50` is too small for suppression to matter against the weapon under test, and the correct response is to raise it — not to loosen the assertion, because the whole squad layer is downstream of that number.

`MenWhoOutrangeTheThreatHoldWhileTheShortRangedOnesClose` depends on Task 1's `SightingMoa` values. If it fails, re-check `CombatValueTests.HoldingBeatsClosingWhenYouOutrangeThem` first — this test cannot pass while that one is failing.

- [ ] **Step 5: Verify the whole fast suite**

Run: `dotnet build DemiurgeSharp.slnx`
Expected: **`MobSystem.cs` will fail to compile** — it still calls the old `SquadTactics.Plan` with `SquadTacticalInput`. That is expected and is Task 7's job. Do not patch it here; confirm the failure is confined to `MobSystem.cs` and the old `SquadTacticalInput`/`FlankSide` references, and move on.

---

### Task 7: Wire the scores into `MobSystem` and delete the branches

The pure layer is complete and tested. This task makes the server use it, and removes the code it replaces.

**Files:**
- Modify: `Server/MobSystem.cs`
- Modify: `Server/Ai/CombatBehavior.cs`
- Modify: `Server/Ai/MobBrain.cs`
- Test: `Server.Tests/CombatBehaviorTests.cs` (existing — update), `Server.Tests/MobNavigationIntegrationTests.cs` (existing — must still pass)

**Interfaces:**
- Consumes: everything from Tasks 1–6.
- Produces: no new public surface. This is integration.

- [ ] **Step 1: Delete the weapon-identity branches**

In `Server/Ai/CombatBehavior.cs`, delete these members entirely:

```
MaxEngagementRangeFor(ItemType)     — replaced by scoring
PrefersToHoldFire(ItemType, float)  — replaced by scoring
ShouldAdvance(float, float)         — replaced by scoring
AimMoaForRange(float)               — replaced by BallisticsStats.SightingMoa
AiAimMoa, LongRangeAimMoa           — replaced by BallisticsStats.SightingMoa
LongRangeStart, LongRangeFullAccuracy
PpshEffectiveRange, DefaultMaxEngagementRange,
SksMaxEngagementRange, MosinMaxEngagementRange
SuppressionBurstShots, BurstPauseTicks,
PrecisionShotIntervalTicks          — replaced by rate-as-a-choice
```

Also delete `MobBrain.BurstShotsRemaining`, `NextBurstTick`, `NextPrecisionShotTick`, `NextSuppressionShotTick` and `ShouldCloseDistance`, and the `ClearCombatTarget` lines that reset them.

- [ ] **Step 2: Fire at the rate the score chose**

In `CombatBehavior`, replace the aim/dispersion block (currently around lines 167–175) with:

```csharp
        var solution = WeaponEffectiveness.Best(
            weapon.Item.Type,
            range,
            targetExposure: brain.PerceivedExposure,
            extraMoa: mob.Spread.TotalMoa(mob.State, ballistics),
            skillFactor: brain.SkillFactor);

        // Cadence comes from the firing solution, not from a burst timer. At range the score picks
        // slow aimed fire because dispersion is a function of rate; up close it picks cyclic because
        // volume is. Fire discipline is therefore chosen, not scheduled.
        uint ticksBetweenShots = solution.ShotsPerSecond <= 0f
            ? uint.MaxValue
            : (uint)MathF.Max(1f, NetworkConfig.TickRate / solution.ShotsPerSecond);

        if (tick < brain.NextShotTick) return true;
        brain.NextShotTick = tick + ticksBetweenShots;
```

Add to `MobBrain`:

```csharp
    /// <summary>Next tick this actor may fire. Derived from the chosen firing solution's rate
    /// rather than from a per-weapon burst schedule.</summary>
    public uint NextShotTick { get; set; }

    /// <summary>Fraction of the believed target's silhouette perception last had a line to. Feeds
    /// the target-radius term in WeaponEffectiveness, so a target in cover is genuinely harder to
    /// hit rather than merely harder to see.</summary>
    public float PerceivedExposure { get; set; } = 1f;

    /// <summary>Scales this actor's sighting error. 1 is a competent soldier. Execution only —
    /// scoring always uses the nominal value.</summary>
    public float SkillFactor { get; set; } = 1f;

    /// <summary>Gunfire this actor remembers, ranked by salience rather than recency.</summary>
    public HeardShots Heard { get; } = new();
```

Delete `HeardActorId`, `HeardPosition`, `HeardTick`, `HeardRevision`, `AppliedHeardRevision`, `HasRecentGunshot` and `ClearGunshot`, replacing their call sites in `MobSystem` with `brain.Heard.TryMostSalient(tick, out var shot)` and `brain.Heard.Clear()`.

- [ ] **Step 3: Adapt `MobSystem.PlanSquadTactics` to the new interface**

In `Server/MobSystem.cs`, replace the body that builds `tacticalInputs` (around line 872) so it constructs `SquadMemberState` instead of `SquadTacticalInput`:

```csharp
            tacticalInputs.Clear();
            foreach (ushort memberId in board.Roster)
            {
                if (!TryGetActor(memberId, out var member)) continue;
                var memberBrain = brains[memberId];

                tacticalInputs.Add(new SquadMemberState(
                    memberId,
                    member.Position,
                    member.EquippedWeaponType,
                    memberBrain.PerceivedExposure,
                    memberBrain.SkillFactor,
                    memberBrain.BoundIndex,
                    memberBrain.MovingSinceTick));
            }

            SquadTactics.Plan(
                new SquadPlanInput(
                    threatPosition,
                    hasThreat,
                    threatWeapon,
                    CombatValue.DefaultAggression,
                    tick),
                tacticalInputs,
                tacticalOrders);
```

Change the field declarations near line 68 to match:

```csharp
        private readonly List<SquadMemberState> tacticalInputs = [];
        private readonly List<SquadTacticalOrder> tacticalOrders = [];
```

Add `MovingSinceTick` to `MobBrain`, set it when a bound order is first issued and cleared on arrival:

```csharp
    /// <summary>Tick this actor's current bound began, or 0 when not moving. SquadTactics uses it to
    /// stop waiting for a mover that cannot arrive.</summary>
    public uint MovingSinceTick { get; set; }
```

- [ ] **Step 4: Remove the cover-query throttle**

`CoverQueriesPerTick = 1` throttles the entire server to 0–3 cover searches per *second*. Measured cost is 0–400 µs/tick inside a 3.9 ms p50 tick against a 33 ms budget, so it is starving behaviour rather than protecting the tick. In `MobSystem.cs` line 22:

```csharp
        /// <summary>
        /// Cover searches allowed per tick, server-wide.
        ///
        /// This was 1, which measured at 0-3 searches per SECOND across every NPC — cover cost
        /// 0-400 us/tick inside a tick running 3.9 ms p50 against a 33 ms budget. It was not
        /// protecting the budget; it was starving the behaviour, and NPCs that could not get a cover
        /// query stood in the open and dug instead.
        ///
        /// Raised deliberately, and the tick percentile line is the gate: if [ServerTick] p99 moves
        /// materially, lower this rather than making the query cheaper.
        /// </summary>
        private const int CoverQueriesPerTick = 8;
```

- [ ] **Step 5: Make squad membership sticky during a committed move**

In `ReformSquads` (around line 810), skip reassignment for a member currently executing a bound, so a replan cannot move a man between squads mid-manoeuvre and reassign his bearing under him:

```csharp
                // A man mid-bound keeps his squad. Re-forming by proximity is right in general, but
                // doing it while he is crossing open ground reassigns his bearing and bound index
                // under him, which is one of the ways a manoeuvre used to dissolve halfway through.
                if (brains.TryGetValue(memberId, out var reformBrain)
                    && reformBrain.MovingSinceTick != 0)
                    continue;
```

- [ ] **Step 6: Build and run the full suite**

Run: `dotnet build DemiurgeSharp.slnx`
Expected: PASS. Fix any remaining references to the deleted members; they should be confined to `MobSystem.cs` and `CombatBehavior.cs`.

Run: `dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"`
Expected: PASS. `CombatBehaviorTests` will need updating where it asserted on the deleted range constants — rewrite those assertions as orderings against `WeaponEffectiveness.Best`, matching the style in `WeaponEffectivenessTests`.

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "Category=Integration"`
Expected: PASS. `MobNavigationIntegrationTests` and `MobTraversalIntegrationTests` exercise movement and must be unaffected; `MobEntrenchmentIntegrationFuzzTests` may need its expectations revisited, since entrenchment is now scored rather than triggered.

---

### Task 8: Verify it against the complaints

The scoring core is in. This task establishes whether it actually fixed the reported behaviour, using the three methods agreed in the spec.

**Files:**
- Create: `Server.Tests/AiBehaviourStatisticsTests.cs`
- Modify: `Server/MobSystem.cs` (stats line), `Client/…` `ai track` overlay

**Interfaces:**
- Consumes: everything above.
- Produces: `MobSystem.BehaviourStats()` returning stationary fraction, voxels dug per NPC per minute, bounds started, mean engagement range.

- [ ] **Step 1: Write the failing statistics test**

Create `Server.Tests/AiBehaviourStatisticsTests.cs`:

```csharp
using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

/// <summary>
/// The complaints, as numbers.
///
/// "They stand at the flagpost doing nothing" and "they dig aimless holes" are aggregate claims, and
/// an impression is a poor regression test — it cannot fail in CI and it cannot say by how much.
/// These run two squads at each other on open ground and measure.
///
/// Deliberately measured from OUTSIDE the AI wherever possible: stationary fraction comes from actor
/// positions and digging comes from ChunkMap.EditVersion, so neither can be satisfied by an AI that
/// merely reports itself busy.
/// </summary>
[Trait("Category", "Integration")]
public class AiBehaviourStatisticsTests
{
    private const int Seconds = 60;
    private const int Ticks = Seconds * NetworkConfig.TickRate;
    private const float StationaryMetresPerTick = 0.05f;

    private sealed record Behaviour(
        float StationaryFraction,
        float VoxelsDugPerNpcPerMinute,
        int BoundsStarted);

    [Fact]
    public void NpcsAreNotStandingAround()
    {
        var behaviour = Run();
        Assert.True(
            behaviour.StationaryFraction < 0.5f,
            $"NPCs were stationary {behaviour.StationaryFraction:P0} of the time");
    }

    [Fact]
    public void NpcsAreNotDiggingAimlessly()
    {
        var behaviour = Run();
        Assert.True(
            behaviour.VoxelsDugPerNpcPerMinute < 5f,
            $"dug {behaviour.VoxelsDugPerNpcPerMinute:0.0} voxels per NPC per minute");
    }

    [Fact]
    public void SquadsActuallyManoeuvre()
        => Assert.True(Run().BoundsStarted > 0, "no squad bounded in a full minute of contact");

    private static Behaviour Run()
    {
        // Flat open soil, wide enough to hold a 70 m engagement: chunkRadius 6 spans +/-96 m, where
        // the default 2 would only reach 32 m and put both squads off the edge of the map.
        var terrain = MobIntegrationTerrain.SoilHeightmap(
            (_, _) => MobIntegrationTerrain.Ground,
            chunkRadius: 6);
        using var harness = new MobIntegrationHarness(terrain, seed: 1234);

        // BOTH sides are AI with weapons. AddEnemy creates a bare actor that never fires, and
        // without incoming fire there is no suppression — which is the term that makes a bound
        // affordable, so a one-sided setup would measure zero manoeuvre for the wrong reason.
        var friendly = new List<ServerPlayer>();
        for (int i = 0; i < 4; i++)
            friendly.Add(harness.AddMob(
                (ushort)(60000 + i),
                new Vector3(i * 4f, MobIntegrationTerrain.Ground, 0f),
                team: 1,
                primary: ItemType.Ak47));
        for (int i = 0; i < 4; i++)
            harness.AddMob(
                (ushort)(61000 + i),
                new Vector3(i * 4f, MobIntegrationTerrain.Ground, 70f),
                team: 2,
                primary: ItemType.Ak47);

        long editsAtStart = terrain.EditVersion;
        var previous = friendly.Select(actor => actor.Position).ToArray();
        long stationarySamples = 0;
        long totalSamples = 0;

        for (uint tick = 1; tick <= Ticks; tick++)
        {
            harness.Step(tick, wallClockDelayMs: 0);

            for (int i = 0; i < friendly.Count; i++)
            {
                var now = friendly[i].Position;
                float moved = Vector2.Distance(
                    new Vector2(now.X, now.Z),
                    new Vector2(previous[i].X, previous[i].Z));

                if (moved < StationaryMetresPerTick) stationarySamples++;
                totalSamples++;
                previous[i] = now;
            }
        }

        // EditVersion increments once per accepted terrain edit, so its delta is the dig count
        // without instrumenting the AI at all.
        long edits = terrain.EditVersion - editsAtStart;

        return new Behaviour(
            stationarySamples / (float)totalSamples,
            edits / (float)friendly.Count / (Seconds / 60f),
            harness.Mobs.BoundsStarted);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~AiBehaviourStatisticsTests"`
Expected: FAIL to compile — `MobSystem.BoundsStarted` does not exist.

- [ ] **Step 3: Add the one counter that cannot be observed from outside**

Stationary fraction and digging are already measurable from actor positions and `ChunkMap.EditVersion`. Manoeuvre is not, so `MobSystem` gains one counter. Add beside the existing `timing*` fields:

```csharp
        /// <summary>
        /// Bounds begun since the server started. Exposed because "did anybody actually manoeuvre"
        /// cannot be observed from outside the AI — unlike stationary time and digging, which
        /// AiBehaviourStatisticsTests reads from actor positions and ChunkMap.EditVersion so they
        /// cannot be satisfied by an AI that merely reports itself busy.
        /// </summary>
        public int BoundsStarted { get; private set; }
```

and increment it at the single place where a bound order is first applied to a brain — where `MovingSinceTick` transitions from 0:

```csharp
                    if (brain.MovingSinceTick == 0)
                    {
                        brain.MovingSinceTick = tick;
                        BoundsStarted++;
                    }
```

- [ ] **Step 4: Run and record the baseline**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~AiBehaviourStatisticsTests"`
Expected: PASS. If a threshold fails, that is a real finding — record the number rather than loosening the assertion, and report it before changing anything.

- [ ] **Step 5: Confirm the performance budget held**

Run: `dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "FullyQualifiedName~ConquestTickBenchmarkTests"`
Expected: the `[ServerTick]` percentile line still reports `within 33.33 ms: 100.0% [OK]`, with p99 not materially above the pre-change 11–15 ms. Per `CLAUDE.md`, percentiles are the requirement, not means.

Also fix the misleading stats label while here — `follow` is currently printed as `movement − combat − entrench`, a residual that silently contains the collision solver and makes path following look 55× more expensive than its actual 51 µs/tick. Report `followerUs` directly and label the remainder `other`.

- [ ] **Step 6: Extend `ai track` and hand over for visual review**

Add role, chosen firing solution (rate and hit probability), and the winning action's score to the `ai track` overlay, so a wrong behaviour can be reported as a wrong number.

Then ask Sebastian to run `dotnet run --launch-profile singleplayer` and look — per `CLAUDE.md`, visual changes are verified by asking, not by screenshotting.

---

## Deliberately not in this plan

Stage 2 (shared trunk paths and wedge steering), Stage 3 (excavation commit model, lazy path validation, 0.5 brush radius, minimum-volume planning), Stage 4 (game-mode-agnostic objectives, force ratio, combat zones) and Stage 5 (grenades, elevation, target-selection scoring) each get their own plan. The squad-in-a-pit dig-thrash scenario belongs to Stage 3, because nothing in this plan changes excavation.
