using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demiurge.Editor;

public static class SourceMapSerializer
{
    private static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static EditorDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<EditorDocument>(stream, options)
            ?? throw new InvalidDataException("Source map is empty");
        var validation = EditorValidation.Validate(document);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));
        return Normalize(document);
    }

    public static byte[] SerializeCanonical(EditorDocument document)
    {
        var normalized = Normalize(document);
        var validation = EditorValidation.Validate(normalized);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));
        return JsonSerializer.SerializeToUtf8Bytes(normalized, options);
    }

    public static byte[] Hash(EditorDocument document)
        => SHA256.HashData(SerializeCanonical(document));

    public static void Save(string path, EditorDocument document)
    {
        byte[] bytes = SerializeCanonical(document);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Source path has no parent directory");
        Directory.CreateDirectory(directory);

        string temporary = fullPath + ".tmp";
        string backup = fullPath + ".bak";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath)) File.Copy(fullPath, backup, overwrite: true);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static EditorDocument Normalize(EditorDocument document)
        => document with
        {
            TerrainStrokes = document.TerrainStrokes
                .OrderBy(stroke => stroke.Sequence).ThenBy(stroke => stroke.Id).ToList(),
            Blocks = document.Blocks
                .OrderBy(block => block.Sequence).ThenBy(block => block.Id).ToList(),
            Placements = document.Placements
                .OrderBy(placement => placement.Kind).ThenBy(placement => placement.Id).ToList(),
        };
}
