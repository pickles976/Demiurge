using System.Numerics;
using Demiurge.GameServer;
using Riptide;

namespace Demiurge.ServerTests;

public class ItemSystemTests
{
    [Fact]
    public void AdministrativeEquipDespawnsReplacedItem()
    {
        var objects = new ObjectReplication(new Server());
        var items = new ItemSystem(objects);
        var actor = new ServerPlayer { Id = 60000 };

        var first = items.SpawnEquipped(actor, ItemType.Ak47, dropReplaced: false);
        var second = items.SpawnEquipped(actor, ItemType.Glock, dropReplaced: false);

        Assert.False(objects.TryGet(first.NetworkId, out _));
        Assert.True(objects.TryGet(second.NetworkId, out var equipped));
        Assert.Equal(actor.Id, equipped.Owner.PlayerId);
        Assert.Equal(ItemType.Glock, equipped.Item.Type);
        Assert.Equal(second.NetworkId, actor.Equipped[EquipSlot.Hand]);
        Assert.Single(objects.All);
    }

    [Fact]
    public void PickupGetsTraitsFromCatalogType()
    {
        var objects = new ObjectReplication(new Server());
        var items = new ItemSystem(objects);

        var pickup = items.SpawnPickup(ItemType.BodyArmor, new Vector3(1, 2, 3));

        Assert.True(pickup.Has.HasFlag(NetComponents.Item));
        Assert.True(pickup.Has.HasFlag(NetComponents.Transform));
        Assert.True(pickup.Has.HasFlag(NetComponents.Armor));
        Assert.False(pickup.Has.HasFlag(NetComponents.Weapon));
        Assert.Equal(ItemType.BodyArmor, pickup.Item.Type);
        Assert.Equal(new Vector3(1, 2, 3), pickup.Transform.Position);
    }
}
