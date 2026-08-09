namespace Demiurge;

/// <summary>Whether changing primary weapons pays for the walk and the lost firing time.</summary>
public static class EquipmentValue
{
    public const float EvaluationSeconds = 30f;
    public const float MinimumGain = 25f;

    public static float NetGain(
        ItemType current,
        ItemType candidate,
        float engagementRange,
        float travelSeconds,
        float skillFactor = 1f)
    {
        float currentDps = WeaponEffectiveness.Best(
            current, engagementRange, TargetExposure.Full, 0f, skillFactor).DamagePerSecond;
        float candidateDps = WeaponEffectiveness.Best(
            candidate, engagementRange, TargetExposure.Full, 0f, skillFactor).DamagePerSecond;

        float usefulSeconds = MathF.Max(0f, EvaluationSeconds - MathF.Max(0f, travelSeconds));
        return candidateDps * usefulSeconds - currentDps * EvaluationSeconds;
    }

    public static bool IsWorthTaking(
        ItemType current,
        ItemType candidate,
        float engagementRange,
        float travelSeconds,
        float skillFactor = 1f)
        => candidate != current
           && ItemCatalog.HasBehavior(candidate, ItemBehavior.Firearm)
           && NetGain(current, candidate, engagementRange, travelSeconds, skillFactor) >= MinimumGain;
}
