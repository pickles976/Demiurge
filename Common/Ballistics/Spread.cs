using System.Numerics;

namespace Demiurge
{
    /// <summary>Pure accuracy math shared by client prediction, server authority, and AI.</summary>
    public static class Spread
    {
        public const float RadiansPerMoa = MathF.PI / (180f * 60f);
        private const float Rayleigh95 = 2.4477468f;

        public static float TotalMoa(float weapon, float stance, float condition, float recoil)
            => MathF.Sqrt(
                weapon * weapon
                + stance * stance
                + condition * condition
                + recoil * recoil);

        public static float Combine(params ReadOnlySpan<float> terms)
        {
            float variance = 0f;
            foreach (float term in terms)
                variance += term * term;
            return MathF.Sqrt(variance);
        }

        /// <summary>
        /// Angular standard deviation for a quoted group diameter containing 95% of shots.
        /// </summary>
        public static float SigmaRadians(float moa)
            => moa * 0.5f * RadiansPerMoa / Rayleigh95;

        /// <summary>
        /// A stable seed lets the local prediction and server sample the same shot without
        /// trusting a client-supplied random direction.
        /// </summary>
        public static uint ShotSeed(ushort shooterId, uint inputSequence)
        {
            uint value = inputSequence ^ ((uint)shooterId << 16) ^ 0x9e3779b9u;
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return value == 0 ? 0x6d2b79f5u : value;
        }

        /// <summary>Samples a circular Gaussian angular error around a normalized aim direction.</summary>
        public static Vector3 SampleDirection(Vector3 aimDirection, float sigmaRadians, uint seed)
        {
            if (!IsFinite(aimDirection) || aimDirection.LengthSquared() < 1e-8f)
                return Vector3.Zero;

            var forward = Vector3.Normalize(aimDirection);
            if (sigmaRadians <= 0f) return forward;

            uint state = seed == 0 ? 0x6d2b79f5u : seed;
            double u1 = (Next(ref state) + 1.0) / (uint.MaxValue + 2.0);
            double u2 = (Next(ref state) + 0.5) / (uint.MaxValue + 1.0);
            double radius = Math.Sqrt(-2.0 * Math.Log(u1));
            double angle = 2.0 * Math.PI * u2;
            float x = sigmaRadians * (float)(radius * Math.Cos(angle));
            float y = sigmaRadians * (float)(radius * Math.Sin(angle));

            var reference = MathF.Abs(forward.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
            var right = Vector3.Normalize(Vector3.Cross(forward, reference));
            var up = Vector3.Cross(right, forward);
            return Vector3.Normalize(forward + right * MathF.Tan(x) + up * MathF.Tan(y));
        }

        private static uint Next(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        private static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    /// <summary>
    /// The decaying parts of a shooter's dispersion. This state belongs to the actor,
    /// not the held weapon, because breathing, stance settling, and suppression survive a swap.
    /// </summary>
    public struct WeaponSpreadState
    {
        private bool stanceInitialized;
        private PlayerStateFlags previousStance;
        private bool movementInitialized;
        private bool wasSprinting;

        public float RecoilMoa { get; private set; }
        public float BreathingMoa { get; private set; }
        public float SuppressionMoa { get; private set; }
        public float StanceChangeMoa { get; private set; }

        public void Advance(PlayerStateFlags state, BallisticsStats weapon, float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0f) return;

            var stance = state & (PlayerStateFlags.Crouching | PlayerStateFlags.Prone);
            if (stanceInitialized && stance != previousStance)
                StanceChangeMoa = BallisticsConfig.StanceChangeMoa;
            stanceInitialized = true;
            previousStance = stance;

            bool sprinting = state.HasFlag(PlayerStateFlags.Sprinting);
            if (movementInitialized && wasSprinting && !sprinting)
                BreathingMoa = BallisticsConfig.PostSprintMoa;
            else if (!sprinting)
                BreathingMoa = Decay(
                    BreathingMoa,
                    BallisticsConfig.PostSprintMoa / BallisticsConfig.PostSprintSeconds,
                    dt);
            movementInitialized = true;
            wasSprinting = sprinting;

            RecoilMoa = Decay(RecoilMoa, weapon.RecoilDecayMoaPerSecond, dt);
            SuppressionMoa = Decay(
                SuppressionMoa,
                BallisticsConfig.SuppressedMoa / BallisticsConfig.SuppressionSeconds,
                dt);
            StanceChangeMoa = Decay(
                StanceChangeMoa,
                BallisticsConfig.StanceChangeMoa / BallisticsConfig.StanceChangeSeconds,
                dt);
        }

        public void AddRecoil(BallisticsStats weapon, PlayerStateFlags state)
        {
            float scale = state.HasFlag(PlayerStateFlags.Prone)
                ? BallisticsConfig.ProneRecoilScale
                : 1f;
            RecoilMoa = MathF.Min(
                weapon.RecoilCapMoa * scale,
                RecoilMoa + weapon.RecoilPerShotMoa * scale);
        }

        public void Suppress()
            => SuppressionMoa = BallisticsConfig.SuppressedMoa;

        /// <summary>
        /// Everything the SHOOTER contributes — his stance, whether he is moving, his breathing, and
        /// what is being fired at him — with nothing about the weapon in it.
        ///
        /// It exists so the AI can score a shot with the same terms it will be taken with.
        /// <see cref="WeaponEffectiveness.Best"/> models the weapon's own group and its recoil
        /// itself, so handing it <see cref="TotalMoa"/> would count both twice; this is exactly the
        /// remainder, and it is what its extraMoa parameter is for.
        /// </summary>
        public readonly float StateMoa(PlayerStateFlags state)
        {
            float stance = state.HasFlag(PlayerStateFlags.Crouching)
                           || state.HasFlag(PlayerStateFlags.Prone)
                ? BallisticsConfig.CrouchedMoa
                : BallisticsConfig.StandingMoa;
            bool sprinting = state.HasFlag(PlayerStateFlags.Sprinting);
            return Spread.Combine(
                stance,
                state.HasFlag(PlayerStateFlags.Aiming) ? 0f : BallisticsConfig.HipFireMoa,
                state.HasFlag(PlayerStateFlags.Moving) && !sprinting ? BallisticsConfig.WalkingMoa : 0f,
                sprinting ? BallisticsConfig.SprintingMoa : 0f,
                BreathingMoa,
                SuppressionMoa,
                StanceChangeMoa);
        }

        public readonly float TotalMoa(PlayerStateFlags state, BallisticsStats weapon)
        {
            float stance = state.HasFlag(PlayerStateFlags.Crouching)
                           || state.HasFlag(PlayerStateFlags.Prone)
                ? BallisticsConfig.CrouchedMoa
                : BallisticsConfig.StandingMoa;
            bool sprinting = state.HasFlag(PlayerStateFlags.Sprinting);
            float condition = Spread.Combine(
                state.HasFlag(PlayerStateFlags.Aiming) ? 0f : BallisticsConfig.HipFireMoa,
                state.HasFlag(PlayerStateFlags.Moving) && !sprinting ? BallisticsConfig.WalkingMoa : 0f,
                sprinting ? BallisticsConfig.SprintingMoa : 0f,
                BreathingMoa,
                SuppressionMoa,
                StanceChangeMoa);
            return Spread.TotalMoa(weapon.BenchMoa, stance, condition, RecoilMoa);
        }

        private static float Decay(float value, float perSecond, float dt)
            => MathF.Max(0f, value - perSecond * dt);
    }
}
