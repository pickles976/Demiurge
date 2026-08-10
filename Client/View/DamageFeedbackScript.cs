using Demiurge.GameClient;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// Everything the player feels about their own health, driven from the one replicated number.
    ///
    /// Kept together rather than split across a shader script, an audio script and a heartbeat
    /// timer, because they are one signal shown four ways: red-out, desaturation, the world going
    /// quiet, and your own pulse. Split up, they drift — three places would each need their own copy
    /// of "how hurt is hurt", and the picture would stop matching the sound.
    ///
    /// Health is server-authoritative and NOT predicted, so this reads what arrived rather than
    /// guessing. That is why the red flash keys off a DROP in the replicated value: it is the only
    /// moment the client knows a wound landed.
    /// </summary>
    public sealed class DamageFeedbackScript : SyncScript
    {
        public required PlayerRegistry Registry { get; init; }

        private const string HeartbeatSound = "assets/sfx/heartbeat.wav";
        private const string HurtSound = "assets/sfx/hurt_sound.wav";

        /// <summary>
        /// How far the world is pushed away at zero health. Not silence: losing the ability to hear
        /// footsteps entirely would be a bigger handicap than the damage itself.
        /// </summary>
        private const float MinimumWorldGain = 0.35f;

        /// <summary>Quietest and loudest the loop gets, from the threshold down to nearly dead.</summary>
        private const float QuietestBeat = 0.30f;
        private const float LoudestBeat = 1f;

        private SoundManager sound = null!;
        private DamageVision? vision;
        private int lastHealth = -1;
        private SoundHandle? heartbeat;

        public override void Start()
        {
            sound = Services.GetSafeServiceAs<SoundManager>();
            // Optional: the transform lives on the graphics compositor, which a headless or
            // embedded configuration may not have built.
            vision = Services.GetService<DamageVision>();
        }

        public override void Update()
        {
            float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;

            if (Registry.LocalPlayer is not { } local || local.Status is not { } status)
            {
                Reset();
                return;
            }

            int current = status.Health.Current;
            int max = status.Health.Max;
            bool alive = !local.IsDead && current > 0;

            // First sighting of a status object is not a wound, and neither is a respawn — both jump
            // the value and only one of them should flash red.
            float lost = lastHealth >= 0 && current < lastHealth
                ? (lastHealth - current) / (float)Math.Max(1, max)
                : 0f;
            lastHealth = current;

            if (lost > 0f)
                sound.PlayOneShot(HurtSound);

            float fraction = HealthConfig.Fraction(current, max);
            vision?.Update(fraction, lost, dt, alive);

            if (!alive)
            {
                sound.WorldGain = 1f;
                StopHeartbeat();
                return;
            }

            UpdateHearing(fraction);
            UpdateHeartbeat(fraction);
        }

        public override void Cancel() => Reset();

        private void Reset()
        {
            lastHealth = -1;
            StopHeartbeat();
            sound.WorldGain = 1f;
            vision?.Update(1f, 0f, 0f, alive: false);
        }

        private void StopHeartbeat()
        {
            if (heartbeat is { } handle) sound.StopContinuous(handle);
            heartbeat = null;
        }

        /// <summary>
        /// The world recedes as you bleed. A genuine low-pass would be better and needs OpenAL's EFX
        /// extension, which this project does not reference — see SoundManager.WorldGain. Gain is
        /// what the engine actually supports, so gain is what this does, and it is applied only to
        /// POSITIONED sound so your own weapon and heartbeat stay present.
        /// </summary>
        private void UpdateHearing(float fraction)
        {
            if (fraction >= HealthConfig.MuffledHearingFraction)
            {
                sound.WorldGain = 1f;
                return;
            }

            float severity = 1f - fraction / HealthConfig.MuffledHearingFraction;
            sound.WorldGain = MathUtil.Lerp(1f, MinimumWorldGain, severity);
        }

        /// <summary>
        /// One LOOPING voice, started when health crosses the threshold and stopped when it
        /// recovers.
        ///
        /// It was a one-shot per beat, which stacked about ten deep: the sample is a ten-second
        /// heartbeat TRACK, not a single thump, so firing it on a sub-second timer piled a fresh
        /// copy on top of the nine still playing. Worth remembering before adding any other
        /// rhythmic cue — check the asset's length before choosing between one-shot and loop.
        /// </summary>
        private void UpdateHeartbeat(float fraction)
        {
            if (fraction >= HealthConfig.HeartbeatFraction)
            {
                StopHeartbeat();
                return;
            }

            // The recording has its own tempo, so severity moves the VOLUME rather than the rate —
            // OpenAL's only rate control is pitch, which would chipmunk it.
            float severity = 1f - fraction / HealthConfig.HeartbeatFraction;
            if (heartbeat is null)
                heartbeat = sound.PlayContinuous(
                    HeartbeatSound,
                    MathUtil.Lerp(QuietestBeat, LoudestBeat, severity));
        }
    }
}
