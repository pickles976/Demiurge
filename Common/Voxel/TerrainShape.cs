namespace Demiurge
{
    /// <summary>
    /// Turns noise parameters into a surface height. This is the terrain design; <see cref="NoiseGen"/>
    /// only supplies the wandering.
    ///
    /// The model is Minecraft's, reduced to its load-bearing part. Noise does not produce height —
    /// noise produces PARAMETERS, and hand-placed curves turn parameters into height:
    ///
    /// - EROSION is the one that matters, a very low frequency field that says how flat this region is.
    ///   Because it is its own noise at its own scale, high-erosion blobs become plains and low-erosion
    ///   blobs become mountain ranges, with no special-casing anywhere — same formula everywhere,
    ///   different parameter value.
    /// - DETAIL is fractal noise, the ordinary bumpiness, scaled by how much relief erosion allows.
    /// - RIDGE is folded noise (see <see cref="NoiseGen"/>), added only where erosion says mountains.
    ///   Plain fractal noise makes rolling blobs; folding it makes crests, which is what a range looks
    ///   like.
    ///
    /// Each of the three splines has a SHELF at the high-erosion end. That is what makes plains
    /// genuinely flat rather than gently hilly — see <see cref="Spline"/> for why that cannot come from
    /// scaling noise down.
    ///
    /// Not modelled yet, and each a separable addition: continentalness (oceans and shorelines),
    /// peaks-and-valleys folding for position within a range, and a 3D density term, which is the only
    /// one of the three that would buy overhangs and caves.
    /// </summary>
    public static class TerrainShape
    {
        /// <summary>
        /// Base height from erosion. Un-eroded ground is high and steep; eroded ground settles onto a
        /// shelf around 34-37, which is the plains altitude.
        /// </summary>
        static readonly Spline BaseHeight = new(
            (-1.00f, 84f),
            (-0.55f, 70f),
            (-0.25f, 50f),      // the drop off the mountains — steep in erosion, so short in world space
            ( 0.00f, 40f),
            ( 0.25f, 37f),      // ---- shelf ----
            ( 1.00f, 34f));

        /// <summary>How much the fractal detail is allowed to move the surface, times detail in [-1, 1].</summary>
        static readonly Spline Relief = new(
            (-1.00f, 14f),
            (-0.50f, 10f),
            (-0.20f,  5f),
            ( 0.05f,  2f),
            ( 0.30f,  1f),      // ---- shelf: plains stay plains ----
            ( 1.00f,  0.8f));

        /// <summary>
        /// Ridge amplitude, times ridge in [0, 1]. Zero across the whole eroded half, so crests appear
        /// on mountains and never in plains — the same trick as Minecraft's jaggedness.
        /// </summary>
        static readonly Spline Jaggedness = new(
            (-1.00f, 16f),
            (-0.45f,  9f),
            (-0.15f,  2f),
            ( 0.10f,  0f),      // ---- shelf at zero ----
            ( 1.00f,  0f));

        /// <summary>
        /// World Y of the surface. <paramref name="erosion"/> and <paramref name="detail"/> are in
        /// [-1, 1]; <paramref name="ridge"/> is in [0, 1].
        /// </summary>
        public static float Height(float erosion, float detail, float ridge)
            => BaseHeight.Evaluate(erosion)
             + Relief.Evaluate(erosion) * detail
             + Jaggedness.Evaluate(erosion) * ridge;

        /// <summary>
        /// Bounds on what <see cref="Height"/> can return, from the splines' own control points. Loose
        /// — it pairs each spline's extreme with every other's, which no single erosion value does — but
        /// that is what makes it a safe budget check: if these fit the world, every real height does.
        /// </summary>
        public static float HighestPossible
            => BaseHeight.MaxOutput + Relief.MaxOutput + Jaggedness.MaxOutput;

        /// <inheritdoc cref="HighestPossible"/>
        public static float LowestPossible
            => BaseHeight.MinOutput - Relief.MaxOutput;
    }
}
