namespace Demiurge
{
    public enum WeaponBallisticsProfile
    {
        SniperRifle,
        SemiAutomaticRifle,
        Carbine,
        Pistol,
        Throwable,
    }

    /// <summary>
    /// Projectile, accuracy, and recoil characteristics shared by a weapon class.
    /// MOA values describe the diameter of the circle containing 95% of shots.
    /// </summary>
    public readonly record struct BallisticsStats(
        float ProjectileSpeed,
        float BenchMoa,
        float RecoilPerShotMoa,
        float RecoilDecayMoaPerSecond,
        float RecoilCapMoa);

    public static class BallisticsConfig
    {
        public const float CrouchedMoa = 14f;
        public const float StandingMoa = 30f;
        public const float HipFireMoa = 150f;
        public const float WalkingMoa = 20f;
        public const float SprintingMoa = 60f;
        public const float PostSprintMoa = 40f;
        public const float PostSprintSeconds = 3f;
        public const float SuppressedMoa = 50f;
        public const float SuppressionSeconds = 2f;
        public const float StanceChangeMoa = 30f;
        public const float StanceChangeSeconds = 0.5f;

        public static WeaponBallisticsProfile? ProfileFor(ItemType type)
            => WeaponConfig.Get(type)?.BallisticsProfile;

        public static BallisticsStats Get(WeaponBallisticsProfile profile) => profile switch
        {
            WeaponBallisticsProfile.SniperRifle => new BallisticsStats(
                ProjectileSpeed: 850f,
                BenchMoa: 2f,
                RecoilPerShotMoa: 70f,
                RecoilDecayMoaPerSecond: 70f,
                RecoilCapMoa: 70f),
            WeaponBallisticsProfile.SemiAutomaticRifle => new BallisticsStats(
                ProjectileSpeed: 800f,
                BenchMoa: 3f,
                RecoilPerShotMoa: 32f,
                RecoilDecayMoaPerSecond: 40f,
                RecoilCapMoa: 190f),
            WeaponBallisticsProfile.Carbine => new BallisticsStats(
                ProjectileSpeed: 715f,
                BenchMoa: 4f,
                RecoilPerShotMoa: 36f,
                RecoilDecayMoaPerSecond: 30f,
                RecoilCapMoa: 220f),
            WeaponBallisticsProfile.Pistol => new BallisticsStats(
                ProjectileSpeed: 375f,
                BenchMoa: 8f,
                RecoilPerShotMoa: 30f,
                RecoilDecayMoaPerSecond: 35f,
                RecoilCapMoa: 150f),
            WeaponBallisticsProfile.Throwable => new BallisticsStats(
                ProjectileSpeed: GrenadeConfig.ThrowSpeed,
                BenchMoa: 0f,
                RecoilPerShotMoa: 0f,
                RecoilDecayMoaPerSecond: 0f,
                RecoilCapMoa: 0f),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null),
        };

        public static BallisticsStats? Get(ItemType type)
            => ProfileFor(type) is { } profile ? Get(profile) : null;

        public static BallisticsStats Require(ItemType type) =>
            Get(type) ?? throw new InvalidOperationException($"{type} has no ballistics stats");
    }
}
