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
        => SourceMapSerializer.Save(Paths.SourcePath(document.Name), document);

    public void SaveAutosave(EditorDocument document)
        => SourceMapSerializer.Save(Paths.AutosavePath(document.Name), document);

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
