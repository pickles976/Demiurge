using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class NpcSquadLoadoutTests
{
    [Fact]
    public void EverySpawnCohortIsTwoAssaultGunsRiflesAndOneBoltGun()
    {
        for (int squad = 0; squad < 4; squad++)
        {
            var weapons = Enumerable.Range(
                    squad * SquadBlackboard.MaximumMembers,
                    SquadBlackboard.MaximumMembers)
                .Select(NpcSquadLoadout.PrimaryForSpawnOrdinal)
                .ToArray();

            Assert.Equal(
                NpcSquadLoadout.AssaultWeaponsPerSquad,
                weapons.Count(type => type == ItemType.Ppsh));
            Assert.Equal(
                NpcSquadLoadout.MarksmenPerSquad,
                weapons.Count(type => type == ItemType.Mosin));
            // Everyone else carries the ordinary rifle: the cohort scales with squad size rather
            // than being a fixed four-man recipe.
            Assert.Equal(
                SquadBlackboard.MaximumMembers
                    - NpcSquadLoadout.AssaultWeaponsPerSquad
                    - NpcSquadLoadout.MarksmenPerSquad,
                weapons.Count(type => type == ItemConfig.DefaultNpcPrimaryWeapon));
        }
    }
}
