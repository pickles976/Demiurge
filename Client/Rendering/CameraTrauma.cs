using Stride.Core.Mathematics;

namespace Demiurge
{
    /// <summary>
    /// Screen shake, held as TRAUMA rather than as an offset.
    ///
    /// Sources add trauma; trauma decays on its own; the shake drawn is trauma SQUARED. That indirection
    /// is the whole design and it buys three things a direct-offset shake cannot:
    ///
    /// - Sources compose. Two rifle shots and a grenade do not fight over one offset, they add to one
    ///   scalar, and the result is a single coherent shake rather than the last writer winning.
    /// - Small things stay small. Squaring means a rifle shot at 0.40 trauma shakes at 16% of full
    ///   while a grenade in your lap shakes at 100% — one constant gives both without a separate
    ///   curve. Worth stating the trap plainly: the same squaring makes it very easy to pick a
    ///   per-source value that rounds to nothing, so the amounts and MaxRoll must be read together.
    /// - It decays in TIME, not per frame, so the feel does not change with the frame rate.
    ///
    /// The displacement itself comes from sampling smooth noise at three unrelated offsets, so the
    /// camera drifts rather than vibrating: white noise per frame reads as a broken monitor, and the
    /// difference between "explosion" and "loose cable" is entirely whether the samples are correlated.
    /// </summary>
    public static class CameraTrauma
    {
        /// <summary>Trauma per second bled off. A shot's worth is gone in about a quarter second —
        /// slower than this and consecutive shots smear into one continuous wobble.</summary>
        private const float DecayPerSecond = 1.6f;

        /// <summary>How fast the noise is walked. High enough to read as a shock, low enough that
        /// consecutive frames stay related.</summary>
        private const float Frequency = 26f;

        /// <summary>
        /// Rotation at FULL trauma. Roll gets the most because it is what sells a shake; yaw and
        /// pitch stay smaller because they read as the aim moving.
        ///
        /// These have to be read together with the squaring, which is easy to get wrong in the
        /// quiet direction: at the first pass a rifle shot contributed 0.22 trauma, which squares to
        /// 0.048, which against a 3.2 degree roll is 0.15 degrees — a shake nobody could see. The
        /// curve is meant to separate a shot from a grenade, not to erase the shot.
        /// </summary>
        private static readonly float MaxYaw = MathUtil.DegreesToRadians(2f);
        private static readonly float MaxPitch = MathUtil.DegreesToRadians(2.5f);
        private static readonly float MaxRoll = MathUtil.DegreesToRadians(6f);

        private static float trauma;
        private static float time;

        /// <summary>0..1. Squared before use — see the class summary.</summary>
        public static float Trauma => trauma;

        /// <summary>
        /// Adds to the pool, clamped. Additive because two sources at once should be worse than
        /// either alone, and clamped because "worse than the worst" has nothing left to express.
        /// </summary>
        public static void Add(float amount)
        {
            if (!float.IsFinite(amount) || amount <= 0f) return;
            trauma = MathUtil.Clamp(trauma + amount, 0f, 1f);
        }

        public static void Reset()
        {
            trauma = 0f;
            time = 0f;
        }

        /// <summary>
        /// Bleeds the pool without producing a rotation. For frames where the camera is not being
        /// driven — terminal open, fly camera, dead — so trauma taken during them does not sit at
        /// full strength waiting to shake the moment control comes back.
        /// </summary>
        public static void Decay(float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0f) return;
            trauma = MathF.Max(0f, trauma - DecayPerSecond * dt);
        }

        /// <summary>Advances the pool and returns this frame's rotation offset. Once per frame.</summary>
        public static Quaternion Update(float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0f) return Quaternion.Identity;

            time += dt;
            Decay(dt);
            if (trauma <= 0f) return Quaternion.Identity;

            float shake = trauma * trauma;
            float t = time * Frequency;

            // Three unrelated slices of the same noise, so the axes are independent without needing
            // three generators.
            return Quaternion.RotationYawPitchRoll(
                Noise(t, 0) * MaxYaw * shake,
                Noise(t, 17) * MaxPitch * shake,
                Noise(t, 41) * MaxRoll * shake);
        }

        /// <summary>
        /// Value noise in [-1, 1]: smooth-stepped interpolation between hashed integers. Cheap, and
        /// unlike a raw hash per frame it is CONTINUOUS, which is what makes the shake read as motion
        /// rather than as static.
        /// </summary>
        private static float Noise(float t, int channel)
        {
            int whole = (int)MathF.Floor(t);
            float frac = t - whole;
            float smooth = frac * frac * (3f - 2f * frac);
            return MathUtil.Lerp(Hash(whole, channel), Hash(whole + 1, channel), smooth);
        }

        private static float Hash(int value, int channel)
        {
            uint h = (uint)(value * 374761393) ^ (uint)(channel * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0x800000 - 1f;
        }
    }
}
