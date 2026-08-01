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

    /// <summary>
    /// Dying just before a wave should not put you straight back in it. The point of a wave is that
    /// reinforcements arrive together and mean something; a man who died a second ago rejoining the
    /// same push has not been punished for dying, and the fight never resets. Under this much left
    /// on the clock, you wait for the one after.
    /// </summary>
    public const int MinimumWaitSeconds = 5;
    public const uint MinimumWaitTicks = MinimumWaitSeconds * NetworkConfig.TickRate;

    /// <summary>
    /// Returns the wave a player dying at <paramref name="tick"/> comes back on: the first one
    /// strictly after it, or the one after THAT when the first is less than
    /// <see cref="MinimumWaitSeconds"/> away.
    /// </summary>
    public static uint NextWaveTick(uint tick)
    {
        ulong wave = (ulong)tick / WaveTicks + 1UL;
        ulong next = wave * WaveTicks;
        if (next - tick < MinimumWaitTicks) next += WaveTicks;
        return next > uint.MaxValue ? uint.MaxValue : (uint)next;
    }
}
