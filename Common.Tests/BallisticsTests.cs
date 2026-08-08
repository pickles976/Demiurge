using System.Numerics;

namespace Demiurge.CommonTests;

public class BallisticsTests
{
    [Fact]
    public void Ak47DealsThirtyDamagePerProjectile()
        => Assert.Equal((ushort)30, WeaponConfig.Require(ItemType.Ak47).Damage);

    [Fact]
    public void MoaConversionUsesGroupDiameterAtNinetyFivePercent()
    {
        float sigma = Spread.SigmaRadians(60f);
        float r95 = sigma * 2.4477468f;

        Assert.Equal(MathF.PI / 360f, r95, 6);
        Assert.Equal(0.95f, HitEstimate.Probability(sigma, 1f, r95), 5);
    }

    [Fact]
    public void IndependentSpreadTermsComposeAsVariances()
    {
        Assert.Equal(5f, Spread.TotalMoa(3f, 4f, 0f, 0f), 5);
        Assert.Equal(13f, Spread.Combine(5f, 12f), 5);
    }

    [Fact]
    public void HitProbabilityFallsWithRangeAndMatchesOneSigma()
    {
        const float sigmaRadians = 0.01f;

        Assert.True(HitEstimate.Probability(sigmaRadians, 10f, 0.6f)
            > HitEstimate.Probability(sigmaRadians, 100f, 0.6f));
        Assert.Equal(1f - MathF.Exp(-0.5f), HitEstimate.Probability(sigmaRadians, 60f, 0.6f), 5);
        Assert.Equal(1f, HitEstimate.Probability(sigmaRadians, 0f, 0.6f));
        Assert.Equal(0f, HitEstimate.Probability(sigmaRadians, 100f, 0f));
    }

    [Fact]
    public void RifleNumbersMatchTheDesignTable()
    {
        var rifle = BallisticsConfig.Require(ItemType.Ak47);
        var settled = new WeaponSpreadState();
        var recoil = new WeaponSpreadState();
        for (int i = 0; i < 20; i++) recoil.AddRecoil(rifle, PlayerStateFlags.None);

        float standing = settled.TotalMoa(PlayerStateFlags.Aiming, rifle);
        float sprinting = settled.TotalMoa(
            PlayerStateFlags.Aiming | PlayerStateFlags.Moving | PlayerStateFlags.Sprinting,
            rifle);
        float hipFire = settled.TotalMoa(PlayerStateFlags.None, rifle);

        Assert.Equal(0.996f, Chance(standing, 100f), 3);
        Assert.Equal(0.68f, Chance(sprinting, 100f), 2);
        Assert.Equal(0.20f, Chance(hipFire, 100f), 2);
        Assert.Equal(0.10f, Chance(recoil.TotalMoa(PlayerStateFlags.Aiming, rifle), 100f), 2);
    }

    [Fact]
    public void RapidCarbineFireBloomsAggressively()
    {
        var carbine = BallisticsConfig.Require(ItemType.Ak47);
        var weapon = WeaponConfig.Require(ItemType.Ak47);
        var state = new WeaponSpreadState();
        var chances = new List<float>();

        for (int shot = 0; shot < 5; shot++)
        {
            chances.Add(Chance(state.TotalMoa(PlayerStateFlags.Aiming, carbine), 100f));
            state.AddRecoil(carbine, PlayerStateFlags.None);
            state.Advance(
                PlayerStateFlags.Aiming,
                carbine,
                weapon.TicksPerShot * NetworkConfig.FixedDt);
        }

        Assert.True(chances[0] > 0.99f);
        Assert.True(chances[2] < 0.70f);
        Assert.True(chances[4] < 0.30f);
        Assert.True(chances.SequenceEqual(chances.OrderDescending()));
    }

    [Fact]
    public void WeaponsMapToReusableBallisticsProfiles()
    {
        Assert.Equal(WeaponBallisticsProfile.SniperRifle, BallisticsConfig.ProfileFor(ItemType.AWP));
        Assert.Equal(WeaponBallisticsProfile.Carbine, BallisticsConfig.ProfileFor(ItemType.Ak47));
        Assert.Equal(WeaponBallisticsProfile.Pistol, BallisticsConfig.ProfileFor(ItemType.Glock));

        var semiAutomatic = BallisticsConfig.Get(WeaponBallisticsProfile.SemiAutomaticRifle);
        Assert.True(semiAutomatic.ProjectileSpeed > BallisticsConfig.Require(ItemType.Glock).ProjectileSpeed);
        Assert.True(semiAutomatic.BenchMoa < BallisticsConfig.Require(ItemType.Ak47).BenchMoa);
    }

    [Fact]
    public void BreathingPenaltyStartsAfterSprintAndDecays()
    {
        var rifle = BallisticsConfig.Require(ItemType.Ak47);
        var state = new WeaponSpreadState();

        state.Advance(PlayerStateFlags.Sprinting, rifle, NetworkConfig.FixedDt);
        Assert.Equal(0f, state.BreathingMoa);

        state.Advance(PlayerStateFlags.None, rifle, NetworkConfig.FixedDt);
        Assert.True(state.BreathingMoa > 39f);

        state.Advance(PlayerStateFlags.None, rifle, BallisticsConfig.PostSprintSeconds);
        Assert.Equal(0f, state.BreathingMoa);
    }

    [Fact]
    public void RecoilAccumulatesToCapAndDecaysToZero()
    {
        var weapon = BallisticsConfig.Require(ItemType.Ak47);
        var state = new WeaponSpreadState();

        for (int i = 0; i < 20; i++)
            state.AddRecoil(weapon, PlayerStateFlags.None);

        Assert.Equal(weapon.RecoilCapMoa, state.RecoilMoa);
        state.Advance(PlayerStateFlags.Aiming, weapon, 10f);
        Assert.Equal(0f, state.RecoilMoa);
        Assert.Equal(
            Spread.TotalMoa(weapon.BenchMoa, BallisticsConfig.StandingMoa, 0f, 0f),
            state.TotalMoa(PlayerStateFlags.Aiming, weapon),
            5);
    }

    [Fact]
    public void ProneReducesRecoilByThirtyPercent()
    {
        var weapon = BallisticsConfig.Require(ItemType.Ak47);
        var standing = new WeaponSpreadState();
        var prone = new WeaponSpreadState();

        standing.AddRecoil(weapon, PlayerStateFlags.None);
        prone.AddRecoil(weapon, PlayerStateFlags.Prone);

        Assert.Equal(
            standing.RecoilMoa * BallisticsConfig.ProneRecoilScale,
            prone.RecoilMoa,
            5);

        for (int i = 0; i < 20; i++)
            prone.AddRecoil(weapon, PlayerStateFlags.Prone);
        Assert.Equal(weapon.RecoilCapMoa * 0.70f, prone.RecoilMoa, 5);
    }

    [Fact]
    public void ShotSamplingIsDeterministicAndKeepsUnitLength()
    {
        uint seed = Spread.ShotSeed(42, 1234);
        var first = Spread.SampleDirection(Vector3.UnitZ, Spread.SigmaRadians(150f), seed);
        var second = Spread.SampleDirection(Vector3.UnitZ, Spread.SigmaRadians(150f), seed);

        Assert.Equal(first, second);
        Assert.Equal(1f, first.Length(), 5);
        Assert.NotEqual(Vector3.UnitZ, first);
    }

    [Fact]
    public void ProjectileMotionAppliesGravityAndHonorsSafetyDistance()
    {
        var first = ProjectileMotion.Advance(Vector3.Zero, new Vector3(10f, 0f, 0f), 0.1f, 100f);
        Assert.Equal(1f, first.End.X, 5);
        Assert.Equal(-0.0981f, first.End.Y, 4);
        Assert.False(first.Exhausted);

        var last = ProjectileMotion.Advance(Vector3.Zero, new Vector3(10f, 0f, 0f), 1f, 2f);
        Assert.Equal(2f, last.Distance, 5);
        Assert.True(last.Exhausted);
    }

    [Fact]
    public void GrenadeThrowSpeedHasFortyMetreLevelRange()
    {
        float levelRange =
            GrenadeConfig.ThrowSpeed * GrenadeConfig.ThrowSpeed / GrenadeConfig.Gravity;

        Assert.Equal(GrenadeConfig.MaxThrowRange, levelRange, 4);
        Assert.Equal(3 * NetworkConfig.TickRate, GrenadeConfig.FuseTicks);
        Assert.Equal((int)(1.5f * NetworkConfig.TickRate), GrenadeConfig.ReloadTicks);
        Assert.Equal(0.5f, GrenadeConfig.TerrainDeformationScale);
        Assert.Equal(1f, GrenadeConfig.DamageFraction(4f));
        Assert.Equal(0.5f, GrenadeConfig.DamageFraction(7f));
        Assert.Equal(0f, GrenadeConfig.DamageFraction(10f));
    }

    private static float Chance(float moa, float range)
        => HitEstimate.Probability(
            Spread.SigmaRadians(moa),
            range,
            GunConfig.HitRadius);
}
