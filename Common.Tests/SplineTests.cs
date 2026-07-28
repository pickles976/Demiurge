namespace Demiurge.Tests;

/// <summary>
/// The spline is where terrain design lives, so it is worth pinning exactly: a wrong interpolation or a
/// wrong clamp is a world that looks plausible and is subtly not what the control points say.
/// </summary>
public class SplineTests
{
    static readonly Spline Ramp = new(
        (-1.0f, 10f),
        ( 0.0f, 20f),
        ( 0.5f, 20f),      // a shelf: half the input range, one output
        ( 1.0f, 60f));

    [Fact]
    public void PassesThroughEveryControlPoint()
    {
        Assert.Equal(10f, Ramp.Evaluate(-1.0f), 4);
        Assert.Equal(20f, Ramp.Evaluate( 0.0f), 4);
        Assert.Equal(20f, Ramp.Evaluate( 0.5f), 4);
        Assert.Equal(60f, Ramp.Evaluate( 1.0f), 4);
    }

    [Theory]
    [InlineData(-0.5f, 15f)]     // midway up the first segment
    [InlineData(-0.25f, 17.5f)]
    [InlineData(0.75f, 40f)]     // midway up the last
    public void InterpolatesLinearlyWithinASegment(float input, float expected)
        => Assert.Equal(expected, Ramp.Evaluate(input), 4);

    /// <summary>
    /// The shelf is the whole reason terrain uses a curve: a wide span of input, one output. Without it
    /// there are no plains, only less-mountainous mountains.
    /// </summary>
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.1f)]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    public void ShelfIsExactlyFlatAcrossItsSpan(float input)
        => Assert.Equal(20f, Ramp.Evaluate(input), 4);

    /// <summary>
    /// Clamps rather than extrapolates. Extrapolating a terrain curve past its ends is how a mountain
    /// ends up poking through the top of the world.
    /// </summary>
    [Theory]
    [InlineData(-5f, 10f)]
    [InlineData(-1.001f, 10f)]
    [InlineData(1.001f, 60f)]
    [InlineData(9000f, 60f)]
    public void ClampsOutsideTheControlRange(float input, float expected)
        => Assert.Equal(expected, Ramp.Evaluate(input), 4);

    [Fact]
    public void ReportsItsOutputBounds()
    {
        Assert.Equal(10f, Ramp.MinOutput, 4);
        Assert.Equal(60f, Ramp.MaxOutput, 4);
    }

    /// <summary>A single point is a constant, which is a legitimate spline and must not divide by zero.</summary>
    [Fact]
    public void SinglePointIsAConstant()
    {
        var flat = new Spline((0f, 7f));

        Assert.Equal(7f, flat.Evaluate(-100f), 4);
        Assert.Equal(7f, flat.Evaluate(0f), 4);
        Assert.Equal(7f, flat.Evaluate(100f), 4);
    }

    /// <summary>
    /// Out-of-order control points would make Evaluate's forward walk wrong and a duplicate input would
    /// divide by zero, so both are rejected at construction rather than producing quiet nonsense.
    /// </summary>
    [Fact]
    public void RejectsPointsThatDoNotAscend()
    {
        Assert.Throws<ArgumentException>(() => new Spline((0f, 1f), (-1f, 2f)));
        Assert.Throws<ArgumentException>(() => new Spline((0f, 1f), (0f, 2f)));
        Assert.Throws<ArgumentException>(() => new Spline());
    }
}
