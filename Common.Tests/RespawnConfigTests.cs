namespace Demiurge.CommonTests;

public class RespawnConfigTests
{
    [Fact]
    public void WaveIsTwentySecondsAtSimulationTickRate()
        => Assert.Equal((uint)(20 * NetworkConfig.TickRate), RespawnConfig.WaveTicks);

    [Theory]
    [InlineData(0u, 600u)]
    [InlineData(1u, 600u)]
    [InlineData(599u, 600u)]
    [InlineData(600u, 1200u)]
    [InlineData(601u, 1200u)]
    public void NextWaveIsSharedBoundaryStrictlyAfterDeath(uint deathTick, uint expected)
        => Assert.Equal(expected, RespawnConfig.NextWaveTick(deathTick));
}
