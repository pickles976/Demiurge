using Demiurge;
using Demiurge.Editor;

string root = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "maps");
var repository = new MapRepository(root);
IReadOnlyList<string> names = args.Length > 1 ? args[1..] : repository.List();

if (names.Count == 0)
{
    Console.Error.WriteLine($"No source maps found under {repository.Paths.Root}");
    return 1;
}

foreach (string name in names)
{
    var document = repository.Load(name);
    var runtime = EditorTerrainEvaluator.Bake(document);
    string output = repository.Paths.RuntimePath(name);
    RuntimeMapSerializer.Save(output, runtime);
    Console.WriteLine($"Baked {name}: {Convert.ToHexString(runtime.ContentHash)}");
}

return 0;
