namespace Demiurge.Tests;

/// <summary>
/// The whole thesis of the AI overhaul, in one unit.
///
/// Every test here asserts that a piece of requested doctrine — "outrange and you hold", "outgunned
/// and you close", "digging is defensive" — falls out of the arithmetic rather than being written
/// down somewhere as a rule. If one of these fails, the currency is wrong, not the tuning.
/// </summary>
public class CombatValueTests
{
    private static Combatant Man(ItemType weapon, float extraMoa = 0f)
        => new(weapon, extraMoa, SkillFactor: 1f);

    private static Engagement Duel(
        ItemType theirWeapon,
        float range,
        float myExposure = 1f,
        float theirExposure = 1f,
        float theirExtraMoa = 0f)
        => new(range, theirWeapon, theirExtraMoa,
            TargetExposure.Of(theirExposure), SelfExposure.Of(myExposure), 1f);

    [Fact]
    public void HoldingBeatsClosingWhenYouOutrangeThem()
    {
        // A bolt gun against an SMG at 100 m. Holding is already near its ceiling; closing hands the
        // SMG its whole advantage.
        var me = Man(ItemType.Mosin);
        float holding = CombatValue.Score(me, [Duel(ItemType.Ppsh, 100f)], CombatValue.DefaultAggression);
        float closed = CombatValue.Score(me, [Duel(ItemType.Ppsh, 15f)], CombatValue.DefaultAggression);

        Assert.True(holding > closed, $"holding {holding:0.0} should beat closing {closed:0.0}");
    }

    [Fact]
    public void ClosingBeatsHoldingWhenTheyOutrangeYou()
    {
        var me = Man(ItemType.Ppsh);
        float holding = CombatValue.Score(me, [Duel(ItemType.Mosin, 100f)], CombatValue.DefaultAggression);
        float closed = CombatValue.Score(me, [Duel(ItemType.Mosin, 15f)], CombatValue.DefaultAggression);

        Assert.True(closed > holding, $"closing {closed:0.0} should beat holding {holding:0.0}");
    }

    [Fact]
    public void CoverIsWorthSomethingEvenWithNoOneShootingBack()
    {
        // This is why Entrench has value at all, and why it has NO value once you are already
        // protected: the term it improves is already at its floor.
        var me = Man(ItemType.Ak47);
        float exposed = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 1f)]);
        float behindCover = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 0.15f)]);

        Assert.True(behindCover < exposed);
        Assert.True(behindCover >= 0f);
    }

    [Fact]
    public void EntrenchingBuysNothingWhenAlreadyProtected()
    {
        // The castle case. Going from 5% exposed to 2% exposed is worth almost nothing, so any
        // action that costs time beats it. No "is there cover nearby" check is involved.
        var me = Man(ItemType.Ak47);
        float alreadySafe = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 0.05f)]);
        float dugIn = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 0.02f)]);
        float inTheOpen = CombatValue.Taken(me, [Duel(ItemType.Ak47, 40f, myExposure: 1f)]);

        float gainWhenSafe = alreadySafe - dugIn;
        float gainWhenExposed = inTheOpen - dugIn;

        Assert.True(
            gainWhenSafe < gainWhenExposed * 0.1f,
            $"digging while protected gains {gainWhenSafe:0.00}, while exposed {gainWhenExposed:0.00}");
    }

    [Fact]
    public void SuppressingThemLowersWhatTheyCanDoToYou()
    {
        // The mechanism the whole squad layer rests on. If this ever stops being true, bounding
        // stops paying and the squad reverts to standing still.
        var me = Man(ItemType.Ak47);
        float unsuppressed = CombatValue.Taken(me, [Duel(ItemType.Mosin, 80f)]);
        float suppressed = CombatValue.Taken(
            me,
            [Duel(ItemType.Mosin, 80f, theirExtraMoa: BallisticsConfig.SuppressedMoa)]);

        Assert.True(
            suppressed < unsuppressed,
            $"suppressed {suppressed:0.00} must be below unsuppressed {unsuppressed:0.00}");
    }

    [Fact]
    public void AnEnemyLookingElsewhereIsLessDangerous()
    {
        var me = Man(ItemType.Ak47);
        var engaged = new Engagement(
            50f, ItemType.Ak47, 0f, TargetExposure.Full, SelfExposure.Full,
            TheirTargetingLikelihood: 1f);
        var distracted = engaged with { TheirTargetingLikelihood = 0.1f };

        Assert.True(CombatValue.Taken(me, [distracted]) < CombatValue.Taken(me, [engaged]));
    }

    [Fact]
    public void AggressionTradesSafetyForContact()
    {
        var me = Man(ItemType.Ppsh);
        var far = new[] { Duel(ItemType.Mosin, 100f) };

        float cautious = CombatValue.Score(me, far, aggression: 0.25f);
        float reckless = CombatValue.Score(me, far, aggression: 4f);

        Assert.True(reckless > cautious, "higher aggression must discount incoming damage");
    }

    [Fact]
    public void MoreEnemiesIsStrictlyWorse()
    {
        var me = Man(ItemType.Ak47);
        var one = new[] { Duel(ItemType.Ak47, 50f) };
        var three = new[] { Duel(ItemType.Ak47, 50f), Duel(ItemType.Ak47, 55f), Duel(ItemType.Ak47, 60f) };

        Assert.True(CombatValue.Taken(me, three) > CombatValue.Taken(me, one));
    }

    [Fact]
    public void NoEnemiesScoresZero()
        => Assert.Equal(0f, CombatValue.Score(Man(ItemType.Ak47), [], CombatValue.DefaultAggression));
}
