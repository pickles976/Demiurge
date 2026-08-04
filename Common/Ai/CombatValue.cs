namespace Demiurge;

/// <summary>One side of a firefight, as scoring needs it.</summary>
/// <param name="Weapon">What they are carrying.</param>
/// <param name="ExtraMoa">Dispersion the caller already knows about — stance, movement,
/// suppression — combined in quadrature with the weapon's own.</param>
/// <param name="SkillFactor">Scales the sighting term. 1 is a competent soldier, above 1 is worse.
/// Deliberately affects execution only: scoring always uses the nominal value, so a poor shot does
/// not correctly reason about being a poor shot.</param>
public readonly record struct Combatant(ItemType Weapon, float ExtraMoa, float SkillFactor);

/// <summary>
/// One believed enemy, as seen from the scoring actor.
/// </summary>
/// <param name="Range">Metres between the two.</param>
/// <param name="TheirWeapon">What perception saw them carrying.</param>
/// <param name="TheirExtraMoa">Their stance/movement/suppression dispersion. Raising this is how
/// covering fire pays for a squadmate's bound.</param>
/// <param name="MyExposureToThem">Fraction of THEIR silhouette I can reach, 0..1.</param>
/// <param name="TheirExposureToMe">Fraction of MY silhouette they can reach, 0..1.</param>
/// <param name="TheirTargetingLikelihood">Probability they are shooting at me rather than at
/// somebody else, 0..1.</param>
public readonly record struct Engagement(
    float Range,
    ItemType TheirWeapon,
    float TheirExtraMoa,
    TargetExposure MyExposureToThem,
    SelfExposure TheirExposureToMe,
    float TheirTargetingLikelihood);

/// <summary>
/// The combat currency: net health points per second.
///
/// This is the unit ARCHITECTURE.md names as the open question — "seconds worked for movement
/// because execution time is a movement's honest cost, and combat has no equally obvious
/// equivalent". It does: the rate at which health changes hands.
///
/// It is what makes actions commensurable that otherwise are not. Holding, closing, flanking,
/// entrenching and suppressing all produce or prevent damage over time, so they can be compared
/// without anybody deciding in advance which one a rifleman should prefer. It also bridges to
/// navigation, which already prices routes in estimated execution seconds: a manoeuvre costing eight
/// seconds costs eight seconds of forgone dealt, plus whatever is taken in transit.
///
/// Pure, and in Common, so the doctrine can be tested headlessly.
/// </summary>
public static class CombatValue
{
    /// <summary>Neutral weighting of damage taken against damage dealt. Raising it makes the whole
    /// force close, flank and sprint; lowering it makes it hold and dig. It is the only global
    /// behaviour knob.</summary>
    public const float DefaultAggression = 1f;

    /// <summary>Health per second this actor puts into the enemies it can reach.</summary>
    public static float Dealt(in Combatant self, IReadOnlyList<Engagement> engagements)
    {
        float total = 0f;
        for (int i = 0; i < engagements.Count; i++)
        {
            var engagement = engagements[i];
            total += WeaponEffectiveness.Best(
                self.Weapon,
                engagement.Range,
                engagement.MyExposureToThem,
                self.ExtraMoa,
                self.SkillFactor).DamagePerSecond;
        }
        return total;
    }

    /// <summary>
    /// Health per second the enemies put into this actor.
    ///
    /// Their skill is deliberately nominal rather than their real skill: an actor cannot know how
    /// good a shot somebody else is, and assuming competence is the safe error.
    /// </summary>
    public static float Taken(in Combatant self, IReadOnlyList<Engagement> engagements)
    {
        float total = 0f;
        for (int i = 0; i < engagements.Count; i++)
        {
            var engagement = engagements[i];
            total += WeaponEffectiveness.Best(
                    engagement.TheirWeapon,
                    engagement.Range,
                    // Perspective flip: from their end I am the target.
                    engagement.TheirExposureToMe.AsTarget(),
                    engagement.TheirExtraMoa,
                    skillFactor: 1f).DamagePerSecond
                * Math.Clamp(engagement.TheirTargetingLikelihood, 0f, 1f);
        }
        return total;
    }

    /// <summary>
    /// Net health per second: what this actor gains minus what it risks, discounted by aggression.
    ///
    /// Every candidate action is scored by building the engagement list it would produce and calling
    /// this. Nothing else decides behaviour.
    /// </summary>
    public static float Score(
        in Combatant self,
        IReadOnlyList<Engagement> engagements,
        float aggression)
        => Dealt(self, engagements)
           - Taken(self, engagements) / MathF.Max(aggression, 0.01f);
}
