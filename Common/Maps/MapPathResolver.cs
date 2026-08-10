namespace Demiurge;

public sealed class MapPathResolver
{
    public string Root { get; }

    public MapPathResolver(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.CurrentDirectory, "maps"));
        StructureRoot = Path.Combine(
            Path.GetDirectoryName(Root) ?? Root,
            "structures");
    }

    /// <summary>
    /// Structures live BESIDE the maps, not inside one.
    ///
    /// A structure is authored once — in the structure editor's flat world, which is not a map at
    /// all — and placed into any map that wants it. Filing it under the map it happened to be drawn
    /// in would make "which bunker do I have" a question with a different answer per map, and would
    /// strand everything built in the scratch world where no real map could reach it.
    /// </summary>
    public string StructureRoot { get; }

    public string StructurePath(string name)
        => Path.Combine(StructureRoot, ValidateName(name) + ".json");

    public IReadOnlyList<string> ListStructures()
    {
        if (!Directory.Exists(StructureRoot)) return [];

        return Directory.EnumerateFiles(StructureRoot, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(IsValidName)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
