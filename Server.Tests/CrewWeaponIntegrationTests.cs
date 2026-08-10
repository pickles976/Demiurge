using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public class CrewWeaponIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void NpcWalksToAndEquipsAnAdvantageousWeapon()
    {
        using var world = new MobIntegrationHarness(
            MobIntegrationTerrain.SoilHeightmap((_, _) => MobIntegrationTerrain.Ground),
            seed: 0xD027);
        var mob = world.AddMob(60000, Vector3.Zero, primary: ItemType.Ppsh);
        world.Items.SpawnPickup(ItemType.Dp27, new Vector3(5f, MobIntegrationTerrain.Ground, 0f));

        for (uint tick = 1; tick <= 300 && Primary(world, mob) != ItemType.Dp27; tick++)
            world.Step(tick, wallClockDelayMs: 2);

        Assert.Equal(ItemType.Dp27, Primary(world, mob));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void NpcMansAndFiresAMortarAtASquadContact()
    {
        using var world = new MobIntegrationHarness(
            MobIntegrationTerrain.SoilHeightmap(
                (_, _) => MobIntegrationTerrain.Ground,
                chunkRadius: 5),
            seed: 0xB00B);
        var gunner = world.AddMob(60000, Vector3.Zero, primary: ItemType.Sks);
        _ = world.AddEnemy(7, new Vector3(0f, 0f, 60f));
        var mortar = world.Items.SpawnPickup(ItemType.Mortar, gunner.Position);
        mortar.Transform.Yaw = 0f;

        bool fired = false;
        for (uint tick = 1; tick <= 300 && !fired; tick++)
        {
            world.Step(tick, wallClockDelayMs: 2);
            fired = gunner.OperatingObjectId == mortar.NetworkId
                && gunner.ReloadDoneTick > tick;
        }

        Assert.True(fired, "the assigned gunner never operated and fired the legal mortar");
    }

    private static ItemType Primary(MobIntegrationHarness world, ServerPlayer actor)
        => world.Weapons.TryGetPrimaryWeapon(actor, out var weapon)
            ? weapon.Item.Type
            : default;
}
