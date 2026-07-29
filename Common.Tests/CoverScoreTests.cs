namespace Demiurge.CommonTests;

public class CoverScoreTests
{
    [Fact]
    public void CrouchBlockedStandingClearIsFightingPosition()
    {
        var rating = CoverScore.Evaluate(new CoverFacts(
            ThreatCount: 2,
            CrouchedExposures: 0,
            StandingExposures: 1,
            CanShootBack: true,
            TravelSeconds: 2f,
            EscapeRoutes: 3));

        Assert.Equal(CoverKind.FightingPosition, rating.Kind);
        Assert.True(rating.Usable);
    }

    [Fact]
    public void BothPosesBlockedIsConcealment()
    {
        var rating = CoverScore.Evaluate(new CoverFacts(
            ThreatCount: 1,
            CrouchedExposures: 0,
            StandingExposures: 0,
            CanShootBack: false,
            TravelSeconds: 1f,
            EscapeRoutes: 2));

        Assert.Equal(CoverKind.Concealment, rating.Kind);
        Assert.True(rating.Usable);
    }

    [Fact]
    public void FullyBlockedPositionWithLateralShotIsCornerFightingPosition()
    {
        var rating = CoverScore.Evaluate(new CoverFacts(
            ThreatCount: 1,
            CrouchedExposures: 0,
            StandingExposures: 0,
            CanShootBack: true,
            TravelSeconds: 2f,
            EscapeRoutes: 2));

        Assert.Equal(CoverKind.CornerFightingPosition, rating.Kind);
        Assert.True(rating.Usable);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    public void ExposedOrInvalidPoseCombinationIsRejected(
        int crouchedExposures,
        int standingExposures,
        bool canShootBack)
    {
        var rating = CoverScore.Evaluate(new CoverFacts(
            ThreatCount: 1,
            CrouchedExposures: crouchedExposures,
            StandingExposures: standingExposures,
            CanShootBack: canShootBack,
            TravelSeconds: 1f,
            EscapeRoutes: 2));

        Assert.Equal(CoverKind.None, rating.Kind);
        Assert.False(rating.Usable);
    }

    [Fact]
    public void ShorterTravelAndMoreEscapesImproveOtherwiseEqualCover()
    {
        var close = CoverScore.Evaluate(Fighting(travel: 1f, escapes: 4));
        var far = CoverScore.Evaluate(Fighting(travel: 8f, escapes: 1));

        Assert.True(close.Score > far.Score);
    }

    [Fact]
    public void FightingPositionOutranksNearbyConcealment()
    {
        var fighting = CoverScore.Evaluate(Fighting(travel: 8f, escapes: 2));
        var concealment = CoverScore.Evaluate(new CoverFacts(
            ThreatCount: 1,
            CrouchedExposures: 0,
            StandingExposures: 0,
            CanShootBack: false,
            TravelSeconds: 0f,
            EscapeRoutes: 4));

        Assert.True(fighting.Score > concealment.Score);
    }

    [Fact]
    public void CornerPeekIsPreferredOverEquivalentTopPeek()
    {
        var top = CoverScore.Evaluate(Fighting(travel: 2f, escapes: 2));
        var corner = CoverScore.Evaluate(new CoverFacts(
            ThreatCount: 1,
            CrouchedExposures: 0,
            StandingExposures: 0,
            CanShootBack: true,
            TravelSeconds: 2f,
            EscapeRoutes: 2));

        Assert.True(corner.Score > top.Score);
    }

    private static CoverFacts Fighting(float travel, int escapes)
        => new(
            ThreatCount: 1,
            CrouchedExposures: 0,
            StandingExposures: 1,
            CanShootBack: true,
            TravelSeconds: travel,
            EscapeRoutes: escapes);
}
