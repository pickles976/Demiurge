namespace Demiurge
{
    public static class HitEstimate
    {
        /// <summary>Chance one shot lands within targetRadius at range. Rayleigh CDF.</summary>
        public static float Probability(float sigmaRadians, float range, float targetRadius)
        {
            if (targetRadius <= 0f) return 0f;
            if (range <= 0f) return 1f;
            float sigma = sigmaRadians * range;
            if (sigma <= 1e-6f) return 1f;
            float k = targetRadius / sigma;
            return 1f - MathF.Exp(-0.5f * k * k);
        }
    }
}
