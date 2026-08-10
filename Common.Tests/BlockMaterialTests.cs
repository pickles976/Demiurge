using Xunit;

namespace Demiurge.Tests;

/// <summary>
/// What each material survives. Written as the RULE — which tool beats which block — rather than as
/// a list of enum members, because the point of the two predicates is that adding a material means
/// answering the question once instead of touching every edit that subtracts.
/// </summary>
public class BlockMaterialTests
{
    [Fact]
    public void AShovelMovesSoilAndTheBagsItIsCarriedIn()
    {
        Assert.True(Blocks.IsSoil(BlockType.BlockType_Grass));
        Assert.True(Blocks.IsSoil(BlockType.BlockType_Dirt));

        // The reason sandbags are on the list: a player who can stack them and not take them down
        // again can permanently wall off ground the terrain rules call diggable.
        Assert.True(Blocks.IsSoil(BlockType.BlockType_Sandbags));
    }

    [Fact]
    public void AShovelDoesNotMoveStoneOfEitherKind()
    {
        Assert.False(Blocks.IsSoil(BlockType.BlockType_Stone));
        Assert.False(Blocks.IsSoil(BlockType.BlockType_StoneBricks));
    }

    /// <summary>
    /// A charge brings a brick wall down and a spade does not; natural rock stops both. That last
    /// one is what keeps the SHAPE of a map a fixed thing rather than something a few grenades can
    /// rewrite.
    /// </summary>
    [Fact]
    public void AnExplosionBeatsMasonryButNotBedrock()
    {
        Assert.True(Blocks.IsBlastable(BlockType.BlockType_StoneBricks));
        Assert.True(Blocks.IsBlastable(BlockType.BlockType_Sandbags));
        Assert.False(Blocks.IsBlastable(BlockType.BlockType_Stone));
    }

    /// <summary>
    /// Timber is the built material a spade is the wrong tool for. It needs no rule of its own —
    /// "un-diggable but blasts like dirt" is just off one list and on the other, which is where
    /// masonry already sits.
    /// </summary>
    [Fact]
    public void AShovelDoesNotMoveTimberButAChargeDoes()
    {
        Assert.False(Blocks.IsSoil(BlockType.BlockType_Wood));
        Assert.True(Blocks.IsBlastable(BlockType.BlockType_Wood));
        Assert.Equal(
            Blocks.IsBlastable(BlockType.BlockType_Dirt),
            Blocks.IsBlastable(BlockType.BlockType_Wood));
    }

    /// <summary>Anything a shovel can move, an explosion can too. The blast list is a superset by
    /// construction, and a future material that broke that would be a rule nobody could explain.</summary>
    [Theory]
    [InlineData(BlockType.BlockType_Air)]
    [InlineData(BlockType.BlockType_Grass)]
    [InlineData(BlockType.BlockType_Dirt)]
    [InlineData(BlockType.BlockType_Stone)]
    [InlineData(BlockType.BlockType_Sandbags)]
    [InlineData(BlockType.BlockType_StoneBricks)]
    [InlineData(BlockType.BlockType_Wood)]
    public void ExplosionsRemoveEverythingAShovelCan(BlockType type)
    {
        if (Blocks.IsSoil(type)) Assert.True(Blocks.IsBlastable(type));

        // And the modes agree with the predicates they are named for, since the edit loop asks the
        // question through CanRemove and nowhere else.
        Assert.Equal(Blocks.IsSoil(type), Blocks.CanRemove(EditMode.SubtractSoil, type));
        Assert.Equal(Blocks.IsBlastable(type), Blocks.CanRemove(EditMode.SubtractBlast, type));

        // An unfiltered subtract is exactly that: the editor's own brush answers to nothing.
        Assert.True(Blocks.CanRemove(EditMode.Subtract, type));
    }

    [Fact]
    public void EveryBlockTypeWithArtResolvesToItsOwnTextures()
    {
        foreach (var type in Enum.GetValues<BlockType>())
        {
            if (type == BlockType.BlockType_Air) continue;
            var entry = BlockTextures.For(type);

            // A type with no manifest row draws the purple prototype, which is the intended
            // missing-art failure — but sandbags and brick have art, so theirs must not be it.
            if (type is BlockType.BlockType_Sandbags
                or BlockType.BlockType_StoneBricks
                or BlockType.BlockType_Wood)
            {
                Assert.True(BlockTextures.Has(type));
                Assert.Equal(4, entry.Variants);
                Assert.DoesNotContain(entry.Paths, path => BlockTextures.Missing.Paths.Contains(path));
            }
        }
    }
}
