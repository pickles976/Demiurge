namespace Demiurge;

public sealed class MapPathResolver
{
    public string Root { get; }

    public MapPathResolver(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.CurrentDirectory, "maps"));
    }

    public string DirectoryFor(string name) => Path.Combine(Root, ValidateName(name));
    public string SourcePath(string name) => Path.Combine(DirectoryFor(name), "source.json");
    public string AutosavePath(string name) => Path.Combine(DirectoryFor(name), "autosave.json");
    public string BackupPath(string name) => Path.Combine(DirectoryFor(name), "source.json.bak");
    public string RuntimePath(string name) => Path.Combine(DirectoryFor(name), "runtime.dmap");

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(Root)) return [];

        return Directory.EnumerateDirectories(Root)
            .Select(Path.GetFileName)
            .Where(name => name is not null && IsValidName(name))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string ValidateName(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException(
                "Map name must contain only ASCII letters, digits, '-' or '_', and be 1-64 characters",
                nameof(name));
        return name;
    }

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
        foreach (char c in name)
            if (!(c is >= 'a' and <= 'z'
                  || c is >= 'A' and <= 'Z'
                  || c is >= '0' and <= '9'
                  || c is '-' or '_'))
                return false;
        return true;
    }
}
