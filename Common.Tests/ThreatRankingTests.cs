namespace Demiurge.Tests;

/// <summary>
/// Which enemy is worth spending a raycast on.
///
/// The point is that this is not a heuristic. Exposure is the only term in Taken that needs a ray,
/// and exposure is at most 1, so the exposure-free product is a genuine UPPER BOUND on what that
/// enemy could contribute. An enemy below the threshold provably cannot change the decision by more
/// than the threshold — which is a much stronger statement than "SMGs far away probably don't
/// matter", and it is what lets the ray budget be spent top-down without hiding a real threat.
/// </summary>
public class ThreatRankingTests
{
    private static Engagement At(ItemType weapon, float range, float exposure = 1f)
        => new(range, weapon, 0f, TargetExposure.Full, SelfExposure.Of(exposure), 1f);

    [Fact]
    public void TheBoundIsNeverBelowTheTrueContribution()
    {
        var me = new Combatant(ItemType.Sks, 0f, 1f);

        foreach (var weapon in new[] { ItemType.Ppsh, ItemType.Sks, ItemType.Sks, ItemType.Mosin })
            for (float range = 5f; range <= 250f; range += 5f)
                foreach (float exposure in new[] { 0f, 0.05f, 0.3f, 0.75f, 1f })
                {
                    var engagement = At(weapon, range, exposure);
                    float bound = ThreatRanking.UpperBound(engagement);
                    float actual = CombatValue.Taken(me, [engagement]);

                    Assert.True(
                        bound >= actual - 1e-3f,
                        $"{weapon} at {range} m, exposure {exposure}: bound {bound:0.000} < actual {actual:0.000}");
                }
    }

    [Fact]
    public void ASubmachineGunAcrossTheMapSinksToTheBottom()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[3];
        int count = ThreatRanking.Rank(
            [
                At(ItemType.Ppsh, 100f),   // index 0 — far SMG, nearly harmless
                At(ItemType.Mosin, 100f),  // index 1 — far bolt gun, dangerous
                At(ItemType.Ppsh, 8f),     // index 2 — SMG in your face
            ],
            ranked);

        Assert.Equal(3, count);
        Assert.Equal(2, ranked[0].Index);
        Assert.Equal(0, ranked[2].Index);
    }

    [Fact]
    public void RankingIsSortedDescending()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[4];
        int count = ThreatRanking.Rank(
            [At(ItemType.Sks, 60f), At(ItemType.Sks, 10f), At(ItemType.Sks, 120f), At(ItemType.Sks, 30f)],
            ranked);

        for (int i = 1; i < count; i++)
            Assert.True(ranked[i - 1].UpperBound >= ranked[i].UpperBound);
    }

    [Fact]
    public void AnEnemyLookingElsewhereRanksLower()
    {
        var focused = At(ItemType.Sks, 40f);
        var distracted = focused with { TheirTargetingLikelihood = 0.05f };

        Assert.True(ThreatRanking.UpperBound(distracted) < ThreatRanking.UpperBound(focused));
    }

    [Fact]
    public void RankingStopsAtTheDestinationCapacity()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[2];
        int count = ThreatRanking.Rank(
            [At(ItemType.Sks, 60f), At(ItemType.Sks, 10f), At(ItemType.Sks, 120f)],
            ranked);

        Assert.Equal(2, count);
        Assert.Equal(1, ranked[0].Index);
    }

    [Fact]
    public void NoThreatsRanksNothing()
    {
        Span<ThreatBound> ranked = stackalloc ThreatBound[4];
        Assert.Equal(0, ThreatRanking.Rank([], ranked));
    }
}
