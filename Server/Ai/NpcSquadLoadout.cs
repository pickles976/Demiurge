namespace Demiurge.GameServer;

/// <summary>
/// Default four-man spawn cohort: two assault guns backed by two intermediate-range rifles.
/// Spawn order is tracked per team, so complete cohorts always contain exactly two of each and a
/// partial cohort receives its assault pair first.
/// </summary>
internal static class NpcSquadLoadout
{
    public const int AssaultWeaponsPerSquad = 2;

    public static ItemType PrimaryForSpawnOrdinal(int ordinal)
    {
        int member = Math.Max(0, ordinal) % SquadBlackboard.MaximumMembers;
        return member < AssaultWeaponsPerSquad
            ? ItemType.Ppsh
            : ItemConfig.DefaultNpcPrimaryWeapon;
    }
}
