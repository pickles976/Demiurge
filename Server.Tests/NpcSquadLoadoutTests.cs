using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class NpcSquadLoadoutTests
{
    [Fact]
    public void EveryFourManSpawnCohortIsTwoAssaultGunsARifleAndOneBoltGun()
    {
        for (int squad = 0; squad < 4; squad++)
        {
            var weapons = Enumerable.Range(
                    squad * SquadBlackboard.MaximumMembers,
                    SquadBlackboard.MaximumMembers)
                .Select(NpcSquadLoadout.PrimaryForSpawnOrdinal)
                .ToArray();

            Assert.Equal(2, weapons.Count(type => type == ItemType.Ppsh));
            Assert.Equal(1, weapons.Count(type => type == ItemType.Sks));
            Assert.Equal(1, weapons.Count(type => type == ItemType.Mosin));
        }
    }
}
