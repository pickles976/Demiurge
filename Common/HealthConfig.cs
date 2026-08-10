namespace Demiurge;

/// <summary>
/// What being hurt costs and how it comes back.
///
/// One place because the server and the client both key off it and must agree: the server owns the
/// number, and the client's red-out, desaturation, muffled hearing and heartbeat are all functions
/// of the same fraction. Split the thresholds and the picture stops matching the health bar.
/// </summary>
public static class HealthConfig
{
    /// <summary>How long after the last wound before it starts closing. Long enough that regen is
    /// something you break contact to earn, not something that happens between shots.</summary>
    public const float RegenerationDelaySeconds = 10f;

    public const uint RegenerationDelayTicks =
        (uint)(RegenerationDelaySeconds * NetworkConfig.TickRate);

    /// <summary>Once it starts, it is quick — the delay is the cost, not the rate.</summary>
    public const float RegenerationPerSecond = 20f;

    /// <summary>
    /// Below this the world loses its colour. Deliberately well under half: desaturation is the
    /// "you are about to die" signal and it means nothing if it is on for most of a firefight.
    /// </summary>
    public const float DesaturationFraction = 0.4f;

    /// <summary>
    /// Below this the ear starts to close up, and it keeps closing all the way down. Higher than the
    /// desaturation point so the audio warns you before the picture does.
    /// </summary>
    public const float MuffledHearingFraction = 0.6f;

    /// <summary>Below this you can hear your own heart.</summary>
    public const float HeartbeatFraction = 0.35f;

    /// <summary>Health as a 0..1 fraction, safe against a zero maximum.</summary>
    public static float Fraction(int current, int max)
        => max <= 0 ? 1f : Math.Clamp(current / (float)max, 0f, 1f);
}
