namespace Demiurge;

/// <summary>
/// Shared timing for the server-authoritative respawn wave. The wave is anchored to server tick
/// zero, so every actor waiting at a boundary respawns together instead of receiving an individual
/// delay from the moment they died.
/// </summary>
public static class RespawnConfig
{
    public const int WaveSeconds = 20;
    public const uint WaveTicks = WaveSeconds * NetworkConfig.TickRate;

    /// <summary>Returns the first wave strictly after <paramref name="tick"/>.</summary>
    public static uint NextWaveTick(uint tick)
    {
        ulong wave = (ulong)tick / WaveTicks + 1UL;
        ulong next = wave * WaveTicks;
        return next > uint.MaxValue ? uint.MaxValue : (uint)next;
    }
}
