namespace Demiurge.Tests;

public class CommandParserTests
{
    [Fact]
    public void ItemCatalogResolvesCanonicalNamesAndAliases()
    {
        Assert.True(ItemCatalog.TryResolve("demiurge:ak47", out var canonical));
        Assert.True(ItemCatalog.TryResolve("AK-47", out var alias));
        Assert.Equal(ItemType.Ak47, canonical);
        Assert.Equal(canonical, alias);
        Assert.Equal("demiurge:body_armor", ItemCatalog.Id(ItemType.BodyArmor));
    }

    [Fact]
    public void EveryWireItemHasExactlyOneCanonicalDefinition()
    {
        var wireTypes = Enum.GetValues<ItemType>().Order().ToArray();
        var catalogTypes = ItemCatalog.All.Select(item => item.Type).Order().ToArray();

        Assert.Equal(wireTypes, catalogTypes);
        Assert.Equal(ItemCatalog.All.Count, ItemCatalog.All.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void ParsesSpawnMobWithOptionalSlash()
    {
        var result = GameCommandParser.Parse("/spawn mob");

        var command = Assert.IsType<SpawnMobCommand>(result.Command);
        Assert.Null(command.Position);
    }

    [Fact]
    public void ParsesPickupWithCanonicalItemAndRelativePosition()
    {
        var result = GameCommandParser.Parse("spawn pickup demiurge:glock ~3 ~-2.5");

        var command = Assert.IsType<SpawnPickupCommand>(result.Command);
        Assert.Equal(ItemType.Glock, command.Item);
        Assert.True(command.Position!.Value.X.Relative);
        Assert.Equal(13f, command.Position.Value.X.Resolve(10f));
        Assert.Equal(17.5f, command.Position.Value.Z.Resolve(20f));
    }

    [Fact]
    public void ParsesSelfAndNumericActorSelectors()
    {
        var self = Assert.IsType<EquipCommand>(
            GameCommandParser.Parse("equip @s ak").Command);
        var mob = Assert.IsType<EquipCommand>(
            GameCommandParser.Parse("equip @60002 body-armor").Command);

        Assert.True(self.Target.IsSelf);
        Assert.Equal(ItemType.Ak47, self.Item);
        Assert.False(mob.Target.IsSelf);
        Assert.Equal((ushort)60002, mob.Target.ActorId);
        Assert.Equal(ItemType.BodyArmor, mob.Item);
    }

    [Theory]
    [InlineData("spawn")]
    [InlineData("spawn mob 1")]
    [InlineData("spawn pickup missing")]
    [InlineData("equip 60000 ak47")]
    [InlineData("spawn mob NaN 0")]
    [InlineData("spawn mob Infinity 0")]
    [InlineData("\"spawn mob")]
    public void RejectsInvalidCommands(string text)
    {
        var result = GameCommandParser.Parse(text);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Error!);
    }

    [Fact]
    public void RejectsCommandsOverTheWireLimit()
    {
        var result = GameCommandParser.Parse(new string('x', GameCommandParser.MaxCommandLength + 1));

        Assert.False(result.Success);
        Assert.Contains("exceeds", result.Error);
    }

    [Fact]
    public void SuggestsPickupGrammarWhenAnItemIsUsedAsTheSpawnKind()
    {
        var result = GameCommandParser.Parse("spawn ak47 0 0");

        Assert.False(result.Success);
        Assert.Equal(
            "'ak47' is an item. Use 'spawn pickup demiurge:ak47 <x> <z>'",
            result.Error);
    }
}
