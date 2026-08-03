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
        /// How close a round has to pass for the man it missed to know about it. Deliberately wider
        /// than the body: a round that misses by a metre is the definition of being shot at, and the
        /// point of suppression is that it costs the target their composure whether or not it
        /// connected. This is a distance from the projectile's PATH, so it is range-independent —
        /// a near miss at four hundred metres reads exactly like one at ten.
        /// </summary>
        public const float NearMissRadius = 2.5f;

        /// <summary>
        /// How close a round has to STRIKE for the dirt it throws up to suppress. Separate from
        /// <see cref="NearMissRadius"/>, and larger, because a round cracking past overhead is a
        /// different experience from one hitting the parapet in front of you — and a shot aimed at
        /// terrain near a man never passes near the man at all, so the fly-by test alone never
        /// noticed it.
        /// </summary>
        public const float ImpactSuppressionRadius = 4f;

        /// <summary>
        /// Height of the head above the feet, as an AI AIM POINT — the part of a man that stays
        /// exposed behind low cover. It sits near the crown rather than at
        /// <see cref="HeadCenterHeight"/> deliberately: it is the height perception needs a clear
        /// line to, and lowering it to the middle of the head sphere would quietly turn every AI
        /// into a headhunter now that <see cref="HeadshotMultiplier"/> exists.
        /// </summary>
        public const float PlayerPeekHeight = 1.45f;

        /// <summary>
        /// The head, as a sphere on the body axis — the one region of an actor that is worth more
        /// than the rest.
        ///
        /// Measured off the player model rather than picked: the vertices skinned to the `head`
        /// joint of cat_orange span y 1.047-1.482 in model space, so the centre is 1.264 up and the
        /// geometry is 0.44 tall by 0.375 wide. The radius is the smaller half-extent, which keeps
        /// the sphere inside the silhouette a shooter is actually looking at — a generous head is
        /// far worse than a tight one, because every metre of it is 2x damage awarded for a shot
        /// that visibly missed.
        ///
        /// It is the MODEL that fixes these numbers, not the collision capsule, whose 1.8 m is
        /// taller than the 1.48 m the player can see. Aim at what is drawn and you hit the head.
        /// </summary>
        public const float HeadCenterHeight = 1.26f;
        public const float HeadRadius = 0.20f;

        /// <summary>
        /// How far the head drops when crouched. The body capsule deliberately ignores crouch — it
        /// lowers the eye, not the volume — but the head cannot: the crouch clip carries the head
        /// bone 0.217 m down, and a sphere left standing would pay 2x for a shot over a crouched
        /// man's head while a hit on the head he can see paid 1x.
        /// </summary>
        public const float CrouchHeadDrop = 0.22f;

        /// <summary>
        /// What a head hit is worth. A multiplier rather than a table so it stays one number for
        /// every weapon: a hit that finds the head is worth more because of where it landed, not
        /// because of what fired it.
        /// </summary>
        public const float HeadshotMultiplier = 2f;

        /// <summary>Centre of the head sphere for an actor standing on <paramref name="feet"/>.</summary>
        public static System.Numerics.Vector3 HeadCenter(System.Numerics.Vector3 feet, bool crouching)
            => feet + new System.Numerics.Vector3(
                0f, crouching ? HeadCenterHeight - CrouchHeadDrop : HeadCenterHeight, 0f);

        /// <summary>Damage after the head multiplier, saturating rather than wrapping.</summary>
        public static ushort Headshot(ushort damage)
            => (ushort)MathF.Min(ushort.MaxValue, damage * HeadshotMultiplier);

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
