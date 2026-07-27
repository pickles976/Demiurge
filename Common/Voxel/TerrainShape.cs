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
        /// Base height from erosion. Three regimes, and the MIDDLE one is easy to lose.
        ///
        /// - Mountains, below about -0.45: high, and the drop off them is steep so the rise is dramatic.
        /// - Foothills, -0.45 to 0.05: a graded ramp from 42 down to 23. This band is the whole reason
        ///   mountains look like they belong to the landscape. Compressing the transition and widening
        ///   the plains shelf at the same time deletes it, and mountains become blobs sitting on flat
        ///   ground — which is exactly what happened once and is why this curve has four control points
        ///   in the middle rather than one.
        /// - Plains, above 0.30: a flat shelf at 20.
        ///
        /// Plains sit LOW, around a fifth of the way up the column, so the world does not float far
        /// above y = 0.
        /// </summary>
        /// <summary>
        /// The floor: plains, and the valley bottoms inside mountain country. Deliberately modest even at
        /// low erosion, because mountain HEIGHT now comes from peaks-and-valleys rather than from here —
        /// if this carried it, ranges would be blob-shaped plateaus again.
        /// </summary>
        static readonly Spline BaseHeight = new(
            (-1.00f, 32f),      // valley floor in mountain country
            (-0.45f, 30f),
            (-0.30f, 28f),      // ---- foothills: a graded band, not a cliff edge ----
            (-0.10f, 24f),
            ( 0.05f, 22f),
            ( 0.30f, 20f),      // ---- shelf: plains ----
            ( 1.00f, 20f));

        /// <summary>
        /// How high a ridge rises above the valley floor, times <c>pv</c>. THIS is what makes chains.
        ///
        /// Erosion decides how tall a chain gets; peaks-and-valleys decides where it runs. The tail
        /// matters as much as the peak — gate this hard on erosion and ranges get clipped to the blob
        /// that raised them, which puts the blobs straight back. Because pv comes from a FOLDED field its high values lie along creases —
        /// curves, not patches — so the mountains follow lines. Put the height on the erosion spline
        /// instead and you get blobs, because gradient noise is isotropic and has no lines in it.
        /// </summary>
        static readonly Spline MountainHeight = new(
            (-1.00f, 56f),
            (-0.60f, 44f),
            (-0.45f, 30f),
            (-0.30f, 16f),
            (-0.10f, 10f),
            ( 0.10f,  6f),      // a long low tail rather than a hard cut-off: a real range runs on past
            ( 0.40f,  3f),      // the region that raised it, and clipping chains to an erosion blob is
            ( 0.70f,  1f),      // exactly what makes them read as blobs again
            ( 1.00f,  0f));

        /// <summary>How much the fractal detail is allowed to move the surface, times detail in [-1, 1].</summary>
        static readonly Spline Relief = new(
            (-1.00f, 12f),
            (-0.55f, 10f),
            (-0.30f,  7f),      // foothills keep real bumpiness, or the band reads as a smooth ramp
            (-0.05f,  4f),
            ( 0.15f,  2f),
            ( 0.40f,  1f),      // ---- shelf: plains stay plains ----
            ( 1.00f,  0.8f));

        /// <summary>
        /// Ridge amplitude, times ridge in [0, 1]. Zero across the eroded half, so crests appear on
        /// mountains and never in plains.
        /// </summary>
        static readonly Spline Jaggedness = new(
            (-1.00f, 14f),
            (-0.55f, 10f),
            (-0.30f,  5f),      // crests carry down into the foothills, then fade
            (-0.05f,  1f),
            ( 0.15f,  0f),      // ---- shelf at zero ----
            ( 1.00f,  0f));

        /// <summary>
        /// Extra sharpening on top of the fold. Now 1, because NoiseGen.RidgeWidth does this job properly
        /// — it thresholds on distance to the crease, which is what actually creates valleys. This was
        /// 1.8 while the fold itself produced no valleys, and it could not rescue it: raising a field
        /// that never drops below 0.4 to a power just makes it a slightly smaller constant.
        /// </summary>
        const float ValleyBroadening = 1.0f;

        /// <summary>
        /// Detail left in a valley floor, as a fraction of what a ridge gets. This is the ridged-
        /// multifractal idea: damp the higher-frequency content where the low-frequency field is low, so
        /// detail concentrates on the ridges and valley bottoms come out smooth rather than lumpy.
        /// </summary>
        const float ValleyDetailDamping = 0.2f;

        /// <summary>
        /// Below this, ground is compressed toward a flat bottom. A backstop for terrain that ends up low
        /// for any reason, not the main mechanism — valleys between ridges are flat because pv goes to
        /// zero there, and this catches whatever that misses. The band sits BELOW the plains shelf so
        /// tuning the plains altitude does not silently squash it.
        /// </summary>
        const float ValleyFloor = 12f;
        const float ValleyFloorBlend = 8f;

        /// <summary>
        /// World Y of the surface. <paramref name="erosion"/> and <paramref name="detail"/> are in
        /// [-1, 1]; <paramref name="pv"/> and <paramref name="ridge"/> are in [0, 1].
        /// </summary>
        public static float Height(float erosion, float pv, float detail, float ridge)
        {
            float crest = MathF.Pow(Math.Clamp(pv, 0f, 1f), ValleyBroadening);

            // Detail follows the ridge: full on a crest, mostly gone on a valley floor.
            float detailAmount = ValleyDetailDamping + (1f - ValleyDetailDamping) * crest;

            float height = BaseHeight.Evaluate(erosion)
                         + MountainHeight.Evaluate(erosion) * crest
                         + Relief.Evaluate(erosion) * detail * detailAmount
                         + Jaggedness.Evaluate(erosion) * ridge * crest;

            return FlattenValleyFloor(height);
        }

        /// <summary>
        /// Compresses ground near <see cref="ValleyFloor"/> toward it. Smoothstep rather than a clamp so
        /// the curve is flat at the bottom (that is the point) and matches slope 1 at the top (so the
        /// blend does not leave a crease across the landscape).
        /// </summary>
        static float FlattenValleyFloor(float height)
        {
            float above = height - ValleyFloor;
            if (above <= 0f) return ValleyFloor;
            if (above >= ValleyFloorBlend) return height;

            float t = above / ValleyFloorBlend;
            return ValleyFloor + above * (t * t * (3f - 2f * t));
        }

        /// <summary>
        /// Bounds on what <see cref="Height"/> can return, from the splines' own control points. Loose
        /// — it pairs each spline's extreme with every other's, which no single erosion value does — but
        /// that is what makes it a safe budget check: if these fit the world, every real height does.
        /// </summary>
        public static float HighestPossible
            => BaseHeight.MaxOutput + MountainHeight.MaxOutput + Relief.MaxOutput + Jaggedness.MaxOutput;

        /// <inheritdoc cref="HighestPossible"/>
        public static float LowestPossible
            => BaseHeight.MinOutput - Relief.MaxOutput;
    }
}
