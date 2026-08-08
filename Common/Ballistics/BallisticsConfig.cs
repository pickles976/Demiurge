namespace Demiurge
{
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

    public static class BallisticsConfig
    {
        public const float CrouchedMoa = 14f;
        public const float StandingMoa = 30f;
        public const float HipFireMoa = 150f;
        public const float WalkingMoa = 20f;
        public const float SprintingMoa = 60f;
        public const float PostSprintMoa = 40f;
        public const float PostSprintSeconds = 3f;

        /// <summary>Supported against the ground, each recoil impulse and its accumulated ceiling
        /// are thirty percent smaller.</summary>
        public const float ProneRecoilScale = 0.70f;
        /// <summary>
        /// Dispersion added while rounds are landing nearby.
        ///
        /// This was 50, which measured at a 3% reduction in a carbine's outgoing damage — a carbine
        /// already carries ~283 MOA of sighting and recoil, and Combine(283, 50) = 287. Suppression
        /// was arithmetically negligible, which meant covering fire did nothing, which meant bounding
        /// never paid for itself and squads stood still. SquadTacticsTests.CoveringFireIsWhat-
        /// MakesABoundAffordable pins this so the number cannot quietly drift back.
        ///
        /// 145 removes about a fifth of a carbine's damage and rather more of a bolt gun's, since it
        /// is added in quadrature and a precision weapon has less inherent dispersion to hide it in.
        /// Suppression therefore costs the marksman more than the sprayer, which is both realistic and
        /// what makes a base of fire worth forming.
        ///
        /// It applies to players as well as NPCs — being shot at degrades your aim by the same
        /// amount, which is the point.
        /// </summary>
        public const float SuppressedMoa = 145f;
        public const float SuppressionSeconds = 2f;
        public const float StanceChangeMoa = 30f;
        public const float StanceChangeSeconds = 0.5f;

        public static string? ProfileFor(ItemType type) => WeaponConfig.Get(type)?.BallisticsId;

        public static BallisticsStats Get(string id) => ItemCatalog.Registry.RequireBallistics(id);

        public static BallisticsStats? Get(ItemType type)
            => ProfileFor(type) is { } profile ? Get(profile) : null;

        public static BallisticsStats Require(ItemType type) =>
            Get(type) ?? throw new InvalidOperationException($"{type} has no ballistics stats");
    }
}
