namespace Demiurge.CommonTests;

public class RespawnConfigTests
{
    [Fact]
    public void WaveIsTwentySecondsAtSimulationTickRate()
        => Assert.Equal((uint)(20 * NetworkConfig.TickRate), RespawnConfig.WaveTicks);

    [Theory]
    [InlineData(0u, 600u)]
    [InlineData(1u, 600u)]
    [InlineData(450u, 600u)]      // exactly MinimumWaitSeconds out: still catches this wave
    [InlineData(600u, 1200u)]
    [InlineData(601u, 1200u)]
    public void NextWaveIsSharedBoundaryStrictlyAfterDeath(uint deathTick, uint expected)
        => Assert.Equal(expected, RespawnConfig.NextWaveTick(deathTick));

    /// <summary>
    /// Dying on the doorstep of a wave should not put you straight back into it — the fight would
    /// never reset and dying would cost nothing.
    /// </summary>
    [Theory]
    [InlineData(451u, 1200u)]
    [InlineData(599u, 1200u)]
    [InlineData(1199u, 1800u)]
    public void DyingJustBeforeAWaveMakesYouWaitForTheNextOne(uint deathTick, uint expected)
        => Assert.Equal(expected, RespawnConfig.NextWaveTick(deathTick));
}
