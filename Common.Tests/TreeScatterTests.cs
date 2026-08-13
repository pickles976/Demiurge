using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Properties of the scatter model, not traces through it. The brush is built on the promise that
/// candidates keep their distance and that the same ground answers the same way however it is
/// approached, so those are what is asserted.
/// </summary>
public class TreeScatterTests
{
    [Theory]
    [InlineData(3f)]
    [InlineData(6f)]
    [InlineData(11.5f)]
    public void CandidatesKeepTheRequestedSpacing(float spacing)
    {
        var candidates = TreeScatter.InCircle(
            new Vector2(120f, -85f), radius: 60f, minSpacing: spacing, density: 1f);

        Assert.NotEmpty(candidates);
        for (int i = 0; i < candidates.Count; i++)
        {
            for (int j = i + 1; j < candidates.Count; j++)
            {
                float distance = Vector2.Distance(candidates[i].Position, candidates[j].Position);
                Assert.True(
                    distance >= spacing - 1e-3f,
                    $"{distance:0.###} m apart, but {spacing:0.###} m was asked for");
            }
        }
    }

    /// <summary>
    /// The lattice is anchored to the world, so a circle centred anywhere proposes the same points
    /// over the ground the two circles share. This is what makes a drag paint a swath instead of a
    /// pile, and it is the property the brush relies on to be idempotent.
    /// </summary>
    [Fact]
    public void OverlappingCirclesAgreeWhereTheyOverlap()
    {
        var left = TreeScatter.InCircle(new Vector2(0f, 0f), 20f, 5f, 1f);
        var right = TreeScatter.InCircle(new Vector2(12f, 3f), 20f, 5f, 1f);

        var shared = left
            .Where(candidate => Vector2.Distance(candidate.Position, new Vector2(12f, 3f)) <= 20f)
            .ToList();

        Assert.NotEmpty(shared);
        foreach (var candidate in shared)
        {
            Assert.Contains(right, other =>
                Vector2.Distance(other.Position, candidate.Position) < 1e-4f
                && MathF.Abs(other.Yaw - candidate.Yaw) < 1e-4f);
        }
    }

    [Fact]
    public void ThinningIsASubsetRatherThanADifferentGrove()
    {
        var full = TreeScatter.InCircle(new Vector2(-40f, 60f), 25f, 6f, 1f);
        var thin = TreeScatter.InCircle(new Vector2(-40f, 60f), 25f, 6f, 0.4f);

        Assert.True(thin.Count < full.Count);
        foreach (var candidate in thin)
            Assert.Contains(full, other => Vector2.Distance(other.Position, candidate.Position) < 1e-4f);
    }

    [Fact]
    public void SpacingSurvivesTheCellSizeInversion()
    {
        // The caller states a spacing and the lattice pitch is derived from it; the two have to
        // agree or the guarantee above is arithmetic that happens to hold.
        const float spacing = 7f;
        float cell = TreeScatter.CellSizeFor(spacing);

        Assert.Equal(spacing, cell * (1f - 2f * TreeScatter.JitterFraction), 3);
    }

    [Fact]
    public void YawCoversTheCircle()
    {
        var candidates = TreeScatter.InCircle(new Vector2(0f, 0f), 80f, 4f, 1f);

        Assert.All(candidates, candidate =>
            Assert.InRange(candidate.Yaw, 0f, MathF.Tau));
        Assert.True(candidates.Max(c => c.Yaw) - candidates.Min(c => c.Yaw) > MathF.PI);
    }
}
