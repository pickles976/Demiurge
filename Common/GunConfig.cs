namespace Demiurge
{
    /// <summary>Shot geometry globals that are NOT per-weapon: HitRadius models
    /// target size (stand-in collider around an object's origin). Per-weapon numbers
    /// live in ItemConfig.
    ///
    /// A shot's ORIGIN is deliberately not here any more. It used to be a MuzzleHeight
    /// constant on the player's centre axis, which put every gun's bullets in the same
    /// wrong place; it now comes from the barrel of the weapon actually held, measured
    /// off that model — client-side, in WeaponMount, since the server only ever
    /// range-checks the origin it is handed and never computes one.</summary>
    public static class GunConfig
    {
        public const float HitRadius = 0.6f;
        public const float PlayerCenterHeight = 0.5f;

        /// <summary>
        /// Height of the head above the feet. Used as an AI aim point, not as a damage multiplier —
        /// there is no headshot, only a part of the body that stays exposed behind low cover.
        /// </summary>
        public const float PlayerPeekHeight = 1.45f;

        private static readonly float[] aimHeights = [PlayerCenterHeight, PlayerPeekHeight];

        /// <summary>
        /// Body points an AI tries to see and shoot, in preference order: centre mass first because it
        /// is the largest target, then the head, so a target peeking over cover with only its head
        /// exposed draws fire instead of being invisible.
        ///
        /// Every entry must lie inside the capsule <see cref="GunMath.PlayerHitDistance"/> tests, or an
        /// AI would settle on a point it can see and provably cannot damage. GunMathTests asserts it.
        /// </summary>
        public static ReadOnlySpan<float> AimHeights => aimHeights;

        /// <summary>
        /// How far a shot's claimed origin may sit from the server's position for that player before
        /// the shot is thrown away. A sanity gate on a client-supplied number, not a tight bound.
        ///
        /// Two terms, and the first is easy to under-budget. A muzzle is wherever the held weapon's
        /// BARREL is, swung by the aim pitch about the chest, so it reaches furthest at extreme
        /// angles rather than level: the longest weapon measures 2.25 m from the player origin at
        /// 83 degrees of pitch, against 1.71 m level. The rest is prediction drift, the same
        /// allowance the flat 2 m here used to be spending entirely on.
        ///
        /// RE-MEASURE THIS when a longer weapon lands — the failure is silent. Shots simply stop
        /// registering at steep angles for that one gun, with no error anywhere.
        /// </summary>
        public const float MaxFireOriginDistance = 4f;
    }
}
