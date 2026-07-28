namespace Demiurge.Editor;

public static class EditorPlacementIds
{
    public const int DisplayLength = 8;

    public static string Display(Guid id)
        => id.ToString("N")[..DisplayLength];

    public static EditorPlacement Resolve(EditorDocument document, string value)
    {
        string token = value.Trim();
        if (Guid.TryParse(token, out var exact))
            return document.Placements.FirstOrDefault(placement => placement.Id == exact)
                ?? throw new ArgumentException($"Placement {value} does not exist");

        if (token.Length < 4 || token.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Placement ID must be a GUID or at least four hexadecimal characters");

        var matches = document.Placements
            .Where(placement => placement.Id.ToString("N").StartsWith(
                token, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"Placement {value} does not exist"),
            _ => throw new ArgumentException($"Placement ID {value} is ambiguous; enter more characters"),
        };
    }
}
