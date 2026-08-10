namespace Demiurge.Editor;

public sealed class MapRepository
{
    public MapPathResolver Paths { get; }

    public MapRepository(string? root = null) => Paths = new MapPathResolver(root);

    public IReadOnlyList<string> List() => Paths.List();

    public EditorDocument New(string name) => EditorDocument.Create(name);

    public EditorDocument Load(string name)
        => SourceMapSerializer.Load(Paths.SourcePath(name));

    public EditorDocument LoadAutosave(string name)
        => SourceMapSerializer.Load(Paths.AutosavePath(name));

    public void Save(EditorDocument document)
        => SourceMapSerializer.Save(Paths.SourcePath(RefuseScratch(document)), document);

    public void SaveAutosave(EditorDocument document)
        => SourceMapSerializer.Save(Paths.AutosavePath(RefuseScratch(document)), document);

    /// <summary>
    /// The structure editor's pad is not a map and never becomes a file.
    ///
    /// Enforced at the write rather than at each caller: there are five paths into these two
    /// methods — the save command, save-as, the editor's own save request, the recovery promotion
    /// and the autosave on shutdown — and a rule stated once at the door cannot be forgotten by the
    /// sixth. Callers still check first where they can give a better answer than an exception.
    /// </summary>
    private static string RefuseScratch(EditorDocument document)
        => document.IsStructureWorld
            ? throw new InvalidOperationException(
                "The structure editor's pad is not a map and cannot be saved. "
                + "Save what you built with 'editor structure save <name>'.")
            : document.Name;

    public bool HasSource(string name) => File.Exists(Paths.SourcePath(name));
    public bool HasAutosave(string name) => File.Exists(Paths.AutosavePath(name));
    public bool HasRuntime(string name) => File.Exists(Paths.RuntimePath(name));

    public bool HasNewerAutosave(string name)
    {
        string autosave = Paths.AutosavePath(name);
        if (!File.Exists(autosave)) return false;
        string source = Paths.SourcePath(name);
        return !File.Exists(source)
            || File.GetLastWriteTimeUtc(autosave) > File.GetLastWriteTimeUtc(source);
    }

    public void DeleteAutosave(string name)
    {
        string path = Paths.AutosavePath(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool RuntimeIsCurrent(EditorDocument document)
    {
        string path = Paths.RuntimePath(document.Name);
        if (!File.Exists(path)) return false;
        try
        {
            var map = RuntimeMapSerializer.Load(path);
            return map.MapId == document.MapId
                && map.SourceHash.SequenceEqual(SourceMapSerializer.Hash(document));
        }
        catch
        {
            return false;
        }
    }
}
