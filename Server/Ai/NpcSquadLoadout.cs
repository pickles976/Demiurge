namespace Demiurge.GameServer;

/// <summary>
/// Default spawn cohort: two assault guns, one bolt gun, and ordinary rifles for the rest.
/// The mix scales with SquadBlackboard.MaximumMembers rather than assuming four men.
/// Spawn order is tracked per team, so complete cohorts always contain exactly that mix and a
/// partial cohort receives its assault pair first — the marksman is the LAST man filled in, because
/// a two-man fire team with a bolt gun in it is short an assault gun rather than long a rifle.
/// </summary>
internal static class NpcSquadLoadout
{
    public const int AssaultWeaponsPerSquad = 2;

    /// <summary>Exactly one man per squad works a bolt. It is a role, not a weapon distribution:
    /// its 150 m ceiling only pays off if the rest of the squad is finding contacts for him.</summary>
    public const int MarksmenPerSquad = 1;

    public static ItemType PrimaryForSpawnOrdinal(int ordinal)
    {
        int member = Math.Max(0, ordinal) % SquadBlackboard.MaximumMembers;
        if (member < AssaultWeaponsPerSquad) return ItemType.Ppsh;
        return member >= SquadBlackboard.MaximumMembers - MarksmenPerSquad
            ? ItemType.Mosin
            : ItemConfig.DefaultNpcPrimaryWeapon;
    }
}
