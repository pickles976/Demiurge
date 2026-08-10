namespace Demiurge.Tests;

public class CompletionTrieTests
{
    private static CompletionTrie Of(params string[] candidates)
    {
        var trie = new CompletionTrie();
        trie.AddRange(candidates);
        return trie;
    }

    [Fact]
    public void EmptyPrefixOffersEverythingSorted()
    {
        var result = Of("spawn", "equip", "ai").Complete(string.Empty);

        Assert.Equal(["ai", "equip", "spawn"], result.Matches);
        Assert.Equal(string.Empty, result.Extension);
    }

    [Fact]
    public void ExtendsToTheLongestSharedPrefixWhenMatchesDiverge()
    {
        var result = Of("session", "set-team", "spawn").Complete("se");

        Assert.Equal(["session", "set-team"], result.Matches);
        Assert.Equal("se", result.Extension);
    }

    [Fact]
    public void ExtendsThroughAnUnbranchedRunOfCharacters()
    {
        var result = Of("playtest", "playtest-networked").Complete("pl");

        Assert.Equal(["playtest", "playtest-networked"], result.Matches);
        Assert.Equal("playtest", result.Extension);
    }

    [Fact]
    public void SingleMatchExtendsToTheWholeCandidate()
    {
        var result = Of("spawn", "equip").Complete("sp");

        Assert.Equal(["spawn"], result.Matches);
        Assert.Equal("spawn", result.Extension);
    }

    /// <summary>
    /// The extension is spelled the way the CANDIDATE is, not the way the query was: completing
    /// "DEM" must produce "demiurge:sks", or Tab would rewrite the line into something the command
    /// parser then has to be case-insensitive about.
    /// </summary>
    [Fact]
    public void ExtensionKeepsTheCandidatesCasing()
    {
        var result = Of("demiurge:sks").Complete("DEM");

        Assert.Equal("demiurge:sks", result.Extension);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndTheFirstSpellingWins()
    {
        var result = Of("Spawn", "spawn", "SPAWN").Complete("s");

        Assert.Equal(["Spawn"], result.Matches);
    }

    [Fact]
    public void UnknownPrefixMatchesNothing()
    {
        var result = Of("spawn", "equip").Complete("zz");

        Assert.Empty(result.Matches);
        Assert.Equal(string.Empty, result.Extension);
    }

    /// <summary>
    /// A candidate that is a prefix of another stops the walk. Extending past "map" would skip the
    /// command the user may have been typing.
    /// </summary>
    [Fact]
    public void StopsAtACandidateThatIsAPrefixOfAnother()
    {
        var result = Of("map", "map-editor").Complete("m");

        Assert.Equal(["map", "map-editor"], result.Matches);
        Assert.Equal("map", result.Extension);
    }
}
