namespace Demiurge;

/// <summary>
/// What a prefix query found: every candidate under it, and how far the typed text can be extended
/// without choosing between them.
/// </summary>
/// <param name="Matches">Candidates beginning with the query, in case-insensitive sorted order.</param>
/// <param name="Extension">
/// The longest prefix every match shares — what Tab should write. Equal to the query when the
/// matches diverge immediately, and equal to a whole candidate when there is only one.
/// </param>
public readonly record struct CompletionResult(IReadOnlyList<string> Matches, string Extension)
{
    public static readonly CompletionResult None = new([], string.Empty);
}

/// <summary>
/// Prefix tree over the completion candidates offered for one token position.
///
/// The tree is not here for lookup speed — the candidate lists are short. It is here because the
/// two things Tab needs are the SAME walk: the matching candidates are the leaves under the
/// query's node, and the text to fill in is however far you can keep walking from that node while
/// there is only one way to go. Filtering with StartsWith and then computing a longest common
/// prefix separately is two passes that can disagree about case handling, which is what this
/// replaces.
///
/// Matching is case-insensitive and the first spelling of a candidate wins, so a list containing
/// both "Spawn" and "spawn" completes to one entry rather than two.
/// </summary>
public sealed class CompletionTrie
{
    private sealed class Node
    {
        // Sorted so a depth-first walk emits matches in order without a sort afterwards.
        public SortedDictionary<char, Node>? Children;
        public string? Word;
    }

    private readonly Node root = new();

    public void Add(string candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return;

        var node = root;
        foreach (char character in candidate)
        {
            node.Children ??= new SortedDictionary<char, Node>();
            char key = char.ToLowerInvariant(character);
            if (!node.Children.TryGetValue(key, out var next))
            {
                next = new Node();
                node.Children[key] = next;
            }
            node = next;
        }
        node.Word ??= candidate;
    }

    public void AddRange(IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates) Add(candidate);
    }

    public CompletionResult Complete(string prefix)
    {
        var node = root;
        foreach (char character in prefix)
        {
            if (node.Children is null
                || !node.Children.TryGetValue(char.ToLowerInvariant(character), out var next))
                return CompletionResult.None;
            node = next;
        }

        var matches = new List<string>();
        Collect(node, matches);
        if (matches.Count == 0) return CompletionResult.None;

        // How far Tab can fill in: keep descending while the path cannot branch and has not already
        // spelled a candidate. Stopping AT a candidate matters — "spawn" and "spawnpoint" must
        // complete to "spawn" rather than skipping past a name the user may have meant.
        int depth = prefix.Length;
        while (node.Word is null && node.Children is { Count: 1 })
        {
            foreach (var child in node.Children.Values) node = child;
            depth++;
        }

        // Taken from a match rather than from the query, so the candidate's own capitalisation
        // survives a differently-cased prefix.
        return new CompletionResult(matches, matches[0][..depth]);
    }

    private static void Collect(Node node, List<string> matches)
    {
        if (node.Word is { } word) matches.Add(word);
        if (node.Children is null) return;
        foreach (var child in node.Children.Values) Collect(child, matches);
    }
}
