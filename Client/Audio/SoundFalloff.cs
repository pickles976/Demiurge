namespace Demiurge
{
    /// <summary>
    /// How far one KIND of sound carries.
    ///
    /// One global curve cannot serve a rifle and a footstep. A curve steep enough that you do not
    /// hear boots from across the map makes gunfire inaudible at a hundred metres; a curve gentle
    /// enough for gunfire puts someone's reload in your ear from two hundred. The real world
    /// separates them by loudness at the source, and this is that, expressed as a falloff per
    /// category rather than a volume per clip.
    ///
    /// The numbers feed OpenAL's inverse-distance-clamped model, where
    /// <c>gain = reference / (reference + rolloff * (distance - reference))</c> and distance is
    /// clamped up to <paramref name="ReferenceDistance"/> — so a sound closer than its reference
    /// plays at full volume. A big reference is therefore "loud and carries", not "quiet nearby".
    /// </summary>
    /// <param name="ReferenceDistance">Within this, full volume.</param>
    /// <param name="RolloffFactor">How fast it falls away past that. Below 1 carries further.</param>
    /// <param name="MaxDistance">
    /// Past this it is not played at all. Not merely an optimisation: the one-shot pool is a fixed
    /// ring of voices, so a sound too far away to matter still claims one and cuts off a near one
    /// that did.
    /// </param>
    public readonly record struct SoundFalloff(
        float ReferenceDistance,
        float RolloffFactor,
        float MaxDistance)
    {
        /// <summary>
        /// Rifle fire: clearly audible right out to the crossover, and then gone.
        ///
        /// The cull distance IS the crossover, taken from WeaponFx rather than written twice. Past
        /// it the distant recording plays instead, and letting the near sample linger would mean
        /// both — or, worse, the near one alone in whatever gap the two constants had drifted into.
        /// </summary>
        public static readonly SoundFalloff Gunshot =
            new(35f, 0.9f, WeaponFx.DistantReportMetres);

        /// <summary>A blast carries further than a rifle, but hands over at the same distance.</summary>
        public static readonly SoundFalloff Explosion =
            new(45f, 0.9f, WeaponFx.DistantReportMetres);

        /// <summary>
        /// A tube firing. The loudest thing on the map and the only one with no distant recording to
        /// hand over to, so it carries the whole way rather than being culled at the crossover — a
        /// mortar you cannot hear firing is one you cannot locate, and locating it is the entire
        /// counter-battery game.
        /// </summary>
        public static readonly SoundFalloff MortarLaunch = new(70f, 0.6f, 260f);

        /// <summary>
        /// The whistle, played at the point the round is coming down on rather than at the round.
        /// Sized to the warning it is: everyone who might be caught should hear it, which is the
        /// blast's damage radius and its dispersion and then some, and nobody else needs to.
        /// </summary>
        public static readonly SoundFalloff MortarIncoming = new(35f, 1.1f, 150f);

        /// <summary>
        /// The far-off recordings. Placed a short way off along the true bearing, so the reference
        /// distance is set well beyond that: the sample already sounds distant, and attenuating it
        /// again for a distance it is not actually at is what made it inaudible.
        /// </summary>
        public static readonly SoundFalloff DistantReport = new(60f, 0.6f, 200f);

        /// <summary>Rounds striking earth near you.</summary>
        public static readonly SoundFalloff Impact = new(12f, 1.4f, 140f);

        /// <summary>A shovel bite — audible as someone working nearby, not across a field.</summary>
        public static readonly SoundFalloff Dig = new(10f, 1.5f, 90f);

        /// <summary>A grenade skittering off the ground. Small, sharp, and short-ranged.</summary>
        public static readonly SoundFalloff Bounce = new(8f, 1.6f, 70f);

        /// <summary>A reload is a tell for whoever is close enough to exploit it, and barely there
        /// by 100 m.</summary>
        public static readonly SoundFalloff Reload = new(6f, 1.8f, 110f);

        /// <summary>Boots. Inside knife-fighting range or not at all.</summary>
        public static readonly SoundFalloff Footstep = new(4f, 2.2f, 32f);

        /// <summary>For anything that has not been given a category yet.</summary>
        public static readonly SoundFalloff Default = new(10f, 1.2f, 150f);
    }
}
