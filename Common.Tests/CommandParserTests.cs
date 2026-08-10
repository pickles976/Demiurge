namespace Demiurge.Tests;

public class CommandParserTests
{
    [Fact]
    public void ItemCatalogResolvesCanonicalNamesAndAliases()
    {
        Assert.True(ItemCatalog.TryResolve("demiurge:ppsh", out var canonical));
        Assert.True(ItemCatalog.TryResolve("PPSh-41", out var alias));
        Assert.Equal(ItemType.Ppsh, canonical);
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

    /// <summary>
    /// A new weapon with no display name would reach a player as "Press E to pick up " — an empty
    /// gap rather than an error, which is the kind of omission nobody notices until it ships.
    /// </summary>
    [Fact]
    public void EveryItemCanBeNamedToAPlayer()
    {
        foreach (var definition in ItemCatalog.All)
            Assert.False(
                string.IsNullOrWhiteSpace(definition.Name),
                $"{definition.Id} has no display name");
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
        var result = GameCommandParser.Parse("spawn pickup demiurge:ppsh ~3 ~-2.5");

        var command = Assert.IsType<SpawnPickupCommand>(result.Command);
        Assert.Equal(ItemType.Ppsh, command.Item);
        Assert.True(command.Position!.Value.X.Relative);
        Assert.Equal(13f, command.Position.Value.X.Resolve(10f));
        Assert.Equal(17.5f, command.Position.Value.Z.Resolve(20f));
    }

    [Fact]
    public void ParsesSelfAndNumericActorSelectors()
    {
        var self = Assert.IsType<EquipCommand>(
            GameCommandParser.Parse("equip @s sks").Command);
        var mob = Assert.IsType<EquipCommand>(
            GameCommandParser.Parse("equip @60002 body-armor").Command);

        Assert.True(self.Target.IsSelf);
        Assert.Equal(ItemType.Sks, self.Item);
        Assert.False(mob.Target.IsSelf);
        Assert.Equal((ushort)60002, mob.Target.ActorId);
        Assert.Equal(ItemType.BodyArmor, mob.Item);
    }

    [Fact]
    public void ParsesAiStats()
        => Assert.IsType<AiStatsCommand>(GameCommandParser.Parse("/ai stats").Command);

    [Theory]
    [InlineData("spawn")]
    [InlineData("spawn mob 1")]
    [InlineData("spawn pickup missing")]
    [InlineData("equip 60000 missing")]
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
        var result = GameCommandParser.Parse("spawn sks 0 0");

        Assert.False(result.Success);
        Assert.Equal(
            "'sks' is an item. Use 'spawn pickup demiurge:sks <x> <z>'",
            result.Error);
    }

    [Theory]
    [InlineData("team @s 2", true, 0, 2)]
    [InlineData("team @60000 1", false, 60000, 1)]
    public void ParsesTeamChanges(string input, bool self, int actorId, int team)
    {
        var result = GameCommandParser.Parse(input);

        var command = Assert.IsType<SetTeamCommand>(result.Command);
        Assert.Equal(self, command.Target.IsSelf);
        if (!self) Assert.Equal((ushort)actorId, command.Target.ActorId);
        Assert.Equal(team, command.Team);
    }

    /// <summary>Zero is the neutral team and negatives are not teams at all. WHICH positive numbers
    /// a map has is the server's business — see ServerCommandServiceTests.</summary>
    [Theory]
    [InlineData("team @s 0")]
    [InlineData("team @s -1")]
    [InlineData("team @s two")]
    [InlineData("team @s")]
    [InlineData("team 60000 2")]
    public void RejectsMalformedTeamChanges(string input)
        => Assert.False(GameCommandParser.Parse(input).Success);
}
